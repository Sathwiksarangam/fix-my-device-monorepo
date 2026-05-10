import 'dart:convert';
import 'dart:async';
import 'package:http/http.dart' as http;

import '../../../auth/data/auth_service.dart';

class ApiDeviceService {
  static const String baseUrl = 'https://fix-my-device-backend-uuu6.onrender.com';
  static const String agentDownloadUrl =
      'https://fix-my-device-backend-uuu6.onrender.com/downloads/FixMyDeviceAgent.exe';

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
          headers: {
            'Authorization': 'Bearer $token',
          },
        )
        .timeout(const Duration(seconds: 30));

    if (response.statusCode == 200) {
      return jsonDecode(response.body) as List<dynamic>;
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
          headers: {
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
