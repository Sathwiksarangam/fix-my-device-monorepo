import 'dart:convert';
import 'package:http/http.dart' as http;

import '../../../auth/data/auth_service.dart';

class ApiDeviceService {
  static const String baseUrl = 'https://fix-my-device-backend.onrender.com';
  static const String agentDownloadUrl =
      'https://fix-my-device-backend.onrender.com/downloads/FixMyDeviceAgent.exe';

  String _requireToken() {
    final token = AuthService.getToken();

    if (token == null || token.isEmpty) {
      throw Exception('Not logged in');
    }

    return token;
  }

  Future<List<dynamic>> getDevices() async {
    final token = _requireToken();

    final response = await http.get(
      Uri.parse('$baseUrl/api/devices'),
      headers: {
        'Authorization': 'Bearer $token',
      },
    );

    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    } else {
      throw Exception('Failed to load devices: ${response.statusCode}');
    }
  }

  Future<String> getAgentSetupCode() async {
    final token = _requireToken();

    final response = await http.get(
      Uri.parse('$baseUrl/api/agent/setup-code'),
      headers: {
        'Authorization': 'Bearer $token',
      },
    );

    if (response.statusCode != 200) {
      throw Exception('Failed to load setup code: ${response.statusCode}');
    }

    final dynamic data = jsonDecode(response.body);
    final String setupCode =
        data['agentSetupCode']?.toString() ??
        AuthService.getAgentSetupCode() ??
        '';

    if (setupCode.isEmpty) {
      throw Exception('Setup code is unavailable');
    }

    return setupCode;
  }
}
