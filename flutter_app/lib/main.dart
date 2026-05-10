import 'package:flutter/material.dart';

import 'app/app.dart';
import 'app/theme/app_theme.dart';
import 'features/auth/data/auth_service.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  ErrorWidget.builder = (FlutterErrorDetails details) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      home: _BootstrapScaffold(
        title: 'Something went wrong',
        message: details.exceptionAsString(),
        isLoading: false,
      ),
    );
  };

  runApp(const FixMyDeviceBootstrap());
}

class FixMyDeviceBootstrap extends StatefulWidget {
  const FixMyDeviceBootstrap({super.key});

  @override
  State<FixMyDeviceBootstrap> createState() => _FixMyDeviceBootstrapState();
}

class _FixMyDeviceBootstrapState extends State<FixMyDeviceBootstrap> {
  late final Future<void> initialization;

  @override
  void initState() {
    super.initState();
    initialization = AuthService.initializeSession();
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<void>(
      future: initialization,
      builder: (BuildContext context, AsyncSnapshot<void> snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return MaterialApp(
            title: 'Fix My Device',
            debugShowCheckedModeBanner: false,
            theme: AppTheme.lightTheme,
            home: const _BootstrapScaffold(
              title: 'Loading Fix My Device',
              message: 'Preparing your dashboard...',
              isLoading: true,
            ),
          );
        }

        if (snapshot.hasError) {
          return MaterialApp(
            title: 'Fix My Device',
            debugShowCheckedModeBanner: false,
            theme: AppTheme.lightTheme,
            home: _BootstrapScaffold(
              title: 'We could not start the app',
              message: snapshot.error.toString(),
              isLoading: false,
            ),
          );
        }

        return const FixMyDeviceApp();
      },
    );
  }
}

class _BootstrapScaffold extends StatelessWidget {
  const _BootstrapScaffold({
    required this.title,
    required this.message,
    required this.isLoading,
  });

  final String title;
  final String message;
  final bool isLoading;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xFFF3F6FA),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 520),
          child: Card(
            child: Padding(
              padding: const EdgeInsets.all(28),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Text(
                    title,
                    style: const TextStyle(
                      fontSize: 26,
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                  const SizedBox(height: 12),
                  Text(message),
                  if (isLoading) ...<Widget>[
                    const SizedBox(height: 20),
                    const CircularProgressIndicator(),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
