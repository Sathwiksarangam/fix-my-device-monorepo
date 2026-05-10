import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../data/auth_service.dart';

class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final TextEditingController emailController = TextEditingController();
  final TextEditingController passwordController = TextEditingController();

  bool isLoading = false;
  bool isPasswordVisible = false;
  String? statusMessage;
  bool isErrorMessage = true;

  bool get isFormEmpty =>
      emailController.text.trim().isEmpty ||
      passwordController.text.trim().isEmpty;

  @override
  void dispose() {
    emailController.dispose();
    passwordController.dispose();
    super.dispose();
  }

  void showMessage(String message, {required bool isError}) {
    setState(() {
      statusMessage = message;
      isErrorMessage = isError;
    });
  }

  Future<void> handleLogin() async {
    if (isFormEmpty) {
      showMessage('Enter email and password.', isError: true);
      return;
    }

    setState(() {
      isLoading = true;
      statusMessage = null;
    });

    final bool success = await AuthService().login(
      emailController.text.trim(),
      passwordController.text.trim(),
    );

    if (!mounted) {
      return;
    }

    setState(() {
      isLoading = false;
    });

    if (success) {
      context.go(AppRoutes.devices);
      return;
    }

    showMessage(
      AuthService.lastErrorMessage ??
          'Wrong password or email not registered.',
      isError: true,
    );
  }

  Future<void> handleSignUp() async {
    if (isFormEmpty) {
      showMessage('Enter email and password.', isError: true);
      return;
    }

    setState(() {
      isLoading = true;
      statusMessage = null;
    });

    final String email = emailController.text.trim();
    final String password = passwordController.text.trim();

    final bool registered = await AuthService().register(email, password);

    if (!mounted) {
      return;
    }

    if (!registered) {
      setState(() {
        isLoading = false;
      });

      showMessage(
        AuthService.lastErrorMessage ??
            'Could not create your account right now. Please try again.',
        isError: true,
      );
      return;
    }

    showMessage(
      'Account created successfully. Logging you in...',
      isError: false,
    );

    await Future<void>.delayed(const Duration(milliseconds: 350));

    if (!mounted) {
      return;
    }

    final bool loggedIn = await AuthService().login(email, password);

    if (!mounted) {
      return;
    }

    setState(() {
      isLoading = false;
    });

    if (loggedIn) {
      context.go(AppRoutes.devices);
      return;
    }

    showMessage(
      AuthService.lastErrorMessage ??
          'Account created, but automatic login failed. Please click Login.',
      isError: true,
    );
  }

  @override
  Widget build(BuildContext context) {
    final ThemeData theme = Theme.of(context);
    final ThemeData loginTheme = theme.copyWith(
      textSelectionTheme: const TextSelectionThemeData(
        cursorColor: Color(0xFF0D63A5),
        selectionColor: Color(0x332166AC),
        selectionHandleColor: Color(0xFF0D63A5),
      ),
      inputDecorationTheme: const InputDecorationTheme(
        filled: true,
        fillColor: Colors.white,
      ),
    );

    return Theme(
      data: loginTheme,
      child: DefaultSelectionStyle(
        selectionColor: const Color(0x332166AC),
        cursorColor: const Color(0xFF0D63A5),
        child: Scaffold(
          backgroundColor: const Color(0xFFF3F6FA),
          body: SafeArea(
            child: Center(
              child: SingleChildScrollView(
                padding: const EdgeInsets.all(24),
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 420),
                  child: Card(
                    child: Padding(
                      padding: const EdgeInsets.all(28),
                      child: AutofillGroup(
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: <Widget>[
                            const Text(
                              'Fix My Device',
                              style: TextStyle(
                                fontSize: 26,
                                fontWeight: FontWeight.bold,
                              ),
                            ),
                            const SizedBox(height: 8),
                            const Text(
                              'Login or create an account to view your private device dashboard.',
                            ),
                            const SizedBox(height: 24),
                            TextFormField(
                              controller: emailController,
                              keyboardType: TextInputType.emailAddress,
                              textInputAction: TextInputAction.next,
                              autofillHints: null,
                              enableSuggestions: false,
                              autocorrect: false,
                              style: const TextStyle(
                                color: Colors.black87,
                                backgroundColor: Colors.transparent,
                              ),
                              cursorColor: const Color(0xFF0D63A5),
                              decoration: const InputDecoration(
                                labelText: 'Email',
                                border: OutlineInputBorder(),
                                filled: true,
                                fillColor: Colors.white,
                              ),
                            ),
                            const SizedBox(height: 16),
                            TextFormField(
                              controller: passwordController,
                              obscureText: !isPasswordVisible,
                              textInputAction: TextInputAction.done,
                              autofillHints: null,
                              enableSuggestions: false,
                              autocorrect: false,
                              style: const TextStyle(
                                color: Colors.black87,
                                backgroundColor: Colors.transparent,
                              ),
                              cursorColor: const Color(0xFF0D63A5),
                              onFieldSubmitted: (_) {
                                if (!isLoading) {
                                  handleLogin();
                                }
                              },
                              decoration: InputDecoration(
                                labelText: 'Password',
                                border: const OutlineInputBorder(),
                                filled: true,
                                fillColor: Colors.white,
                                suffixIcon: IconButton(
                                  onPressed: () {
                                    setState(() {
                                      isPasswordVisible = !isPasswordVisible;
                                    });
                                  },
                                  icon: Icon(
                                    isPasswordVisible
                                        ? Icons.visibility_off_outlined
                                        : Icons.visibility_outlined,
                                  ),
                                  tooltip: isPasswordVisible
                                      ? 'Hide password'
                                      : 'Show password',
                                ),
                              ),
                            ),
                            if (statusMessage != null) ...<Widget>[
                              const SizedBox(height: 12),
                              Container(
                                width: double.infinity,
                                padding: const EdgeInsets.all(12),
                                decoration: BoxDecoration(
                                  color: isErrorMessage
                                      ? const Color(0xFFFDECEA)
                                      : const Color(0xFFEAF6EC),
                                  borderRadius: BorderRadius.circular(12),
                                  border: Border.all(
                                    color: isErrorMessage
                                        ? const Color(0xFFF3C7C1)
                                        : const Color(0xFFB9DEC0),
                                  ),
                                ),
                                child: Row(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: <Widget>[
                                    Icon(
                                      isErrorMessage
                                          ? Icons.error_outline_rounded
                                          : Icons.check_circle_outline_rounded,
                                      size: 18,
                                      color: isErrorMessage
                                          ? const Color(0xFFB42318)
                                          : const Color(0xFF1F7A38),
                                    ),
                                    const SizedBox(width: 10),
                                    Expanded(
                                      child: Text(
                                        statusMessage!,
                                        style:
                                            theme.textTheme.bodyMedium?.copyWith(
                                          color: isErrorMessage
                                              ? const Color(0xFF7A271A)
                                              : const Color(0xFF1D5E2A),
                                          fontWeight: FontWeight.w600,
                                        ),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ],
                            if (kIsWeb) const SizedBox(height: 4),
                            const SizedBox(height: 24),
                            SizedBox(
                              width: double.infinity,
                              child: ElevatedButton(
                                onPressed: isLoading ? null : handleLogin,
                                child: isLoading
                                    ? const SizedBox(
                                        width: 18,
                                        height: 18,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2,
                                        ),
                                      )
                                    : const Text('Login'),
                              ),
                            ),
                            const SizedBox(height: 12),
                            SizedBox(
                              width: double.infinity,
                              child: OutlinedButton(
                                onPressed: isLoading ? null : handleSignUp,
                                child: const Text('Sign Up'),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
