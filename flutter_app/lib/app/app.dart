import 'package:flutter/material.dart';

import 'router/app_router.dart';
import 'theme/app_theme.dart';

class FixMyDeviceApp extends StatelessWidget {
  const FixMyDeviceApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp.router(
      title: 'Fix My Device',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.lightTheme,
      routerConfig: AppRouter.router,
      builder: (BuildContext context, Widget? child) {
        return ColoredBox(
          color: const Color(0xFFF3F6FA),
          child: child ?? const _AppShellFallback(),
        );
      },
    );
  }
}

class _AppShellFallback extends StatelessWidget {
  const _AppShellFallback();

  @override
  Widget build(BuildContext context) {
    return const Center(
      child: CircularProgressIndicator(),
    );
  }
}
