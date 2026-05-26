import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import '../../../auth/data/auth_service.dart';
import '../../../files/data/models/recovery_models.dart';

class ApiDeviceService {
  static const String baseUrl = 'https://fix-my-device-monorepo.onrender.com';
  static const String agentInstallerDownloadUrl =
      String.fromEnvironment('FMD_AGENT_INSTALLER_URL', defaultValue: '');
  static const String resetAgentCommand =
      r'Remove-Item "$env:APPDATA\Fix My Device Agent\agent-config.json" -Force -ErrorAction SilentlyContinue';

  String _requireToken() {
    final token = AuthService.getToken();

    if (token == null || token.isEmpty) {
      throw Exception('Not logged in');
    }

    return token;
  }

  Future<List<dynamic>> getDevices() async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/devices'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode == 200) {
      final dynamic data = jsonDecode(response.body);
      if (data is List<dynamic>) {
        return data;
      }

      return <dynamic>[];
    }

    throw Exception(_extractErrorMessage(
      response,
      fallback: 'Failed to load devices.',
    ));
  }

  Future<String> getAgentSetupCode() async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/agent/setup-code'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to load setup code.',
      ));
    }

    final dynamic data = jsonDecode(response.body);
    final String setupCode = data['agentSetupCode']?.toString() ??
        AuthService.getAgentSetupCode() ??
        '';

    if (setupCode.isEmpty) {
      throw Exception('Setup code is unavailable.');
    }

    return setupCode;
  }

  Future<RecoverySettings> getRecoverySettings(String deviceId) async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/recovery/settings?deviceId=$deviceId'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to load emergency recovery settings.',
      ));
    }

    final dynamic data = jsonDecode(response.body);
    if (data is! Map<String, dynamic>) {
      throw Exception('Emergency recovery settings are unavailable.');
    }

    return RecoverySettings.fromJson(data);
  }

  Future<RecoverySettings> saveRecoverySettings({
    required String deviceId,
    required String deviceName,
    required bool enabled,
    required List<RecoveryApprovedLocation> approvedLocations,
  }) async {
    final token = _requireToken();

    final response = await http
        .post(
          Uri.parse('$baseUrl/api/recovery/settings'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
            'Content-Type': 'application/json',
          },
          body: jsonEncode(<String, dynamic>{
            'deviceId': deviceId,
            'deviceName': deviceName,
            'enabled': enabled,
            'approvedLocations': approvedLocations
                .map((RecoveryApprovedLocation location) => location.toJson())
                .toList(),
          }),
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to save emergency recovery settings.',
      ));
    }

    final dynamic data = jsonDecode(response.body);
    if (data is! Map<String, dynamic>) {
      throw Exception('Emergency recovery settings could not be saved.');
    }

    return RecoverySettings(
      deviceId: deviceId,
      deviceName: deviceName,
      enabled: enabled,
      approvedLocations: approvedLocations,
      lastSyncedAt: '',
    );
  }

  Future<List<RecoveryFileEntry>> getRecoveryFileList(String deviceId) async {
    final RecoveryInventory inventory = await getRecoveryInventory(deviceId);
    return inventory.files;
  }

  Future<List<RecoveryFileEntry>> getRecoveryFiles(String deviceId) async {
    return getRecoveryFileList(deviceId);
  }

  Future<RecoveryInventory> getRecoveryInventory(String deviceId) async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/recovery/$deviceId'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to load emergency recovery inventory.',
      ));
    }

    final dynamic data = jsonDecode(response.body);
    if (data is! Map<String, dynamic>) {
      throw Exception('Emergency recovery inventory is unavailable.');
    }

    return RecoveryInventory.fromJson(data);
  }

  Future<void> requestRecoveryDownload({
    required String deviceId,
    required String filePath,
  }) async {
    final token = _requireToken();

    final response = await http
        .post(
          Uri.parse('$baseUrl/api/transfers/download-from-device'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
            'Content-Type': 'application/json',
          },
          body: jsonEncode(<String, dynamic>{
            'deviceId': deviceId,
            'filePath': filePath,
          }),
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to create recovery download request.',
      ));
    }
  }

  Future<void> uploadFileToDevice({
    required String deviceId,
    required String destinationPath,
    required String fileName,
    required Uint8List bytes,
  }) async {
    final token = _requireToken();
    final request = http.MultipartRequest(
      'POST',
      Uri.parse('$baseUrl/api/transfers/upload-to-device'),
    );
    request.headers['Authorization'] = 'Bearer $token';
    request.fields['deviceId'] = deviceId;
    request.fields['destinationPath'] = destinationPath;
    request.files.add(http.MultipartFile.fromBytes(
      'file',
      bytes,
      filename: fileName,
    ));

    final streamedResponse =
        await request.send().timeout(const Duration(seconds: 60));
    final response = await http.Response.fromStream(streamedResponse);

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to create upload-to-device request.',
      ));
    }
  }

  Future<List<TransferJob>> getTransferHistory(String deviceId) async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/transfers/history?deviceId=$deviceId'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to load transfer history.',
      ));
    }

    final dynamic data = jsonDecode(response.body);
    if (data is! List<dynamic>) {
      return <TransferJob>[];
    }

    return data
        .whereType<Map<String, dynamic>>()
        .map(TransferJob.fromJson)
        .toList();
  }

  Future<TransferDownload> downloadTransferFile(String jobId) async {
    final token = _requireToken();

    final response = await http
        .get(
          Uri.parse('$baseUrl/api/transfers/$jobId/download'),
          headers: <String, String>{
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 60));

    if (response.statusCode != 200) {
      throw Exception(_extractErrorMessage(
        response,
        fallback: 'Failed to download the completed transfer.',
      ));
    }

    final String disposition = response.headers['content-disposition'] ?? '';
    final RegExpMatch? match =
        RegExp('filename="([^"]+)"').firstMatch(disposition);
    final String fileName = match?.group(1) ?? 'FixMyDeviceDownload.bin';

    return TransferDownload(fileName: fileName, bytes: response.bodyBytes);
  }

  Future<TransferJob?> waitForCompletedDownloadJob({
    required String deviceId,
    required String filePath,
    Duration timeout = const Duration(seconds: 90),
  }) async {
    final DateTime startedAt = DateTime.now();

    while (DateTime.now().difference(startedAt) < timeout) {
      final List<TransferJob> jobs = await getTransferHistory(deviceId);
      for (final TransferJob job in jobs) {
        if (job.jobType != 'download_from_device') {
          continue;
        }

        if (job.requestedFilePath != filePath) {
          continue;
        }

        if (job.isCompleted || job.isFailed) {
          return job;
        }
      }

      await Future<void>.delayed(const Duration(seconds: 3));
    }

    return null;
  }

  String _extractErrorMessage(
    http.Response response, {
    required String fallback,
  }) {
    try {
      final dynamic data = jsonDecode(response.body);
      if (data is Map<String, dynamic>) {
        final String? message = data['message']?.toString();
        final String? detail = data['detail']?.toString();
        final String? title = data['title']?.toString();
        final String? best = message ?? detail ?? title;

        if (best != null && best.trim().isNotEmpty) {
          return best.trim();
        }
      }
    } catch (_) {
      if (response.body.trim().isNotEmpty) {
        return response.body.trim();
      }
    }

    return '$fallback (${response.statusCode})';
  }
}
