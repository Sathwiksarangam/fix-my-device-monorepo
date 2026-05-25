import 'dart:convert';
import 'dart:async';
import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';

class AuthService {
  static const String baseUrl = 'https://fix-my-device-backend.onrender.com';
  static const String _tokenKey = 'auth_token';
  static const String _emailKey = 'auth_email';
  static const String _agentSetupCodeKey = 'agent_setup_code';

  static String? token;
  static String? email;
  static String? agentSetupCode;
  static String? lastErrorMessage;
  static final ValueNotifier<int> authState = ValueNotifier<int>(0);

  static Future<void> initializeSession() async {
    final SharedPreferences preferences = await SharedPreferences.getInstance();

    token = preferences.getString(_tokenKey);
    email = preferences.getString(_emailKey);
    agentSetupCode = preferences.getString(_agentSetupCodeKey);
    lastErrorMessage = null;
  }

  static Future<void> _saveSession() async {
    final SharedPreferences preferences = await SharedPreferences.getInstance();

    if (token != null && token!.isNotEmpty) {
      await preferences.setString(_tokenKey, token!);
    } else {
      await preferences.remove(_tokenKey);
    }

    if (email != null && email!.isNotEmpty) {
      await preferences.setString(_emailKey, email!);
    } else {
      await preferences.remove(_emailKey);
    }

    if (agentSetupCode != null && agentSetupCode!.isNotEmpty) {
      await preferences.setString(_agentSetupCodeKey, agentSetupCode!);
    } else {
      await preferences.remove(_agentSetupCodeKey);
    }
  }

  Future<bool> register(String userEmail, String password) async {
    lastErrorMessage = null;

    try {
      final response = await http
          .post(
            Uri.parse('$baseUrl/api/auth/register'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({
              'email': userEmail,
              'password': password,
            }),
          )
          .timeout(const Duration(seconds: 30));

      debugPrint('Register response status: ${response.statusCode}');
      debugPrint('Register response body: ${response.body}');

      if (response.statusCode == 200 || response.statusCode == 201) {
        return true;
      }

      try {
        final dynamic data = jsonDecode(response.body);
        final String? message = data is Map<String, dynamic>
            ? data['message']?.toString()
            : null;

        if (message == 'User registered successfully') {
          return true;
        }
      } catch (_) {
        if (response.body.contains('User registered successfully')) {
          return true;
        }
      }

      lastErrorMessage = _extractErrorMessage(
        response,
        fallback: 'Could not create your account right now. Please try again.',
      );
      return false;
    } on TimeoutException {
      lastErrorMessage = 'The server took too long to respond. Please try again.';
      return false;
    } catch (_) {
      lastErrorMessage =
          'We could not reach the Fix My Device service. Check your connection and try again.';
      return false;
    }
  }

  Future<bool> login(String userEmail, String password) async {
    lastErrorMessage = null;

    try {
      final response = await http
          .post(
            Uri.parse('$baseUrl/api/auth/login'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({
              'email': userEmail,
              'password': password,
            }),
          )
          .timeout(const Duration(seconds: 30));

      if (response.statusCode == 200) {
        final data = jsonDecode(response.body);
        token = data['token']?.toString();
        email = data['email']?.toString();
        agentSetupCode = data['agentSetupCode']?.toString();
        await _saveSession();
        authState.value++;
        return true;
      }

      lastErrorMessage = _extractErrorMessage(
        response,
        fallback: 'Wrong password or email not registered.',
      );
      return false;
    } on TimeoutException {
      lastErrorMessage = 'The server took too long to respond. Please try again.';
      return false;
    } catch (_) {
      lastErrorMessage =
          'We could not reach the Fix My Device service. Check your connection and try again.';
      return false;
    }
  }

  static String? getToken() => token;
  static String? getEmail() => email;
  static String? getAgentSetupCode() => agentSetupCode;
  static bool get isLoggedIn =>
      token != null && token!.isNotEmpty && email != null && email!.isNotEmpty;

  static Future<void> logout() async {
    token = null;
    email = null;
    agentSetupCode = null;
    lastErrorMessage = null;
    await _saveSession();
    authState.value++;
  }

  static String _extractErrorMessage(
    http.Response response, {
    required String fallback,
  }) {
    if (response.statusCode == 401) {
      return 'Wrong password or email not registered.';
    }

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

    return fallback;
  }
}
