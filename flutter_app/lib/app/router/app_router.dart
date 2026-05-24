import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../features/auth/data/auth_service.dart';
import '../../features/auth/presentation/screens/login_screen.dart';
import '../../features/dashboard/presentation/screens/dashboard_screen.dart';
import '../../features/devices/presentation/screens/device_details_screen.dart';
import '../../features/devices/presentation/screens/devices_list_screen.dart';
import '../../features/files/presentation/screens/file_browser_screen.dart';
import '../../features/files/presentation/screens/file_transfer_screen.dart';
import '../../features/settings/presentation/screens/settings_screen.dart';
import '../../features/troubleshooting/presentation/screens/troubleshooting_screen.dart';

class AppRoutes {
  static const login = '/';
  static const dashboard = '/dashboard';
  static const devices = '/devices';
  static const deviceDetails = '/devices/details';
  static const emergencyRecovery = '/recovery';
  static const legacyFileBrowser = '/files/browser';
  static const fileTransfer = '/files/transfer';
  static const troubleshooting = '/troubleshooting';
  static const settings = '/settings';
}

class AppRouter {
  static final GoRouter router = GoRouter(
    initialLocation:
        AuthService.isLoggedIn ? AppRoutes.devices : AppRoutes.login,
    refreshListenable: AuthService.authState,
    redirect: (BuildContext context, GoRouterState state) {
      final bool loggedIn = AuthService.isLoggedIn;
      final bool isLoggingIn = state.matchedLocation == AppRoutes.login;

      if (!loggedIn && !isLoggingIn) {
        return AppRoutes.login;
      }

      if (loggedIn && isLoggingIn) {
        return AppRoutes.devices;
      }

      return null;
    },
    errorBuilder: (BuildContext context, GoRouterState state) {
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
                    const Text(
                      'We could not open that page',
                      style: TextStyle(
                        fontSize: 24,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: 10),
                    Text(
                      state.error?.toString() ??
                          'The requested route is unavailable right now.',
                    ),
                    const SizedBox(height: 20),
                    FilledButton(
                      onPressed: () => router.go(
                        AuthService.isLoggedIn
                            ? AppRoutes.devices
                            : AppRoutes.login,
                      ),
                      child: const Text('Back To Safety'),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );
    },
    routes: <RouteBase>[
      GoRoute(
        path: AppRoutes.login,
        builder: (BuildContext context, GoRouterState state) {
          return const LoginScreen();
        },
      ),
      GoRoute(
        path: AppRoutes.dashboard,
        builder: (BuildContext context, GoRouterState state) {
          return const DashboardScreen();
        },
      ),
      GoRoute(
        path: AppRoutes.devices,
        builder: (BuildContext context, GoRouterState state) {
          return const DevicesListScreen();
        },
      ),
      GoRoute(
        path: AppRoutes.deviceDetails,
        builder: (BuildContext context, GoRouterState state) {
          final String? deviceId = state.uri.queryParameters['id'];
          return DeviceDetailsScreen(deviceId: deviceId);
        },
      ),
      GoRoute(
        path: AppRoutes.emergencyRecovery,
        builder: (BuildContext context, GoRouterState state) {
          final String? deviceId = state.uri.queryParameters['id'];
          return FileBrowserScreen(deviceId: deviceId);
        },
      ),
      GoRoute(
        path: AppRoutes.legacyFileBrowser,
        builder: (BuildContext context, GoRouterState state) {
          final String? deviceId = state.uri.queryParameters['id'];
          return FileBrowserScreen(deviceId: deviceId);
        },
      ),
      GoRoute(
        path: AppRoutes.fileTransfer,
        builder: (BuildContext context, GoRouterState state) {
          final String? deviceId = state.uri.queryParameters['id'];
          return FileTransferScreen(deviceId: deviceId);
        },
      ),
      GoRoute(
        path: AppRoutes.troubleshooting,
        builder: (BuildContext context, GoRouterState state) {
          final String? deviceId = state.uri.queryParameters['id'];
          return TroubleshootingScreen(deviceId: deviceId);
        },
      ),
      GoRoute(
        path: AppRoutes.settings,
        builder: (BuildContext context, GoRouterState state) {
          return const SettingsScreen();
        },
      ),
    ],
  );
}
