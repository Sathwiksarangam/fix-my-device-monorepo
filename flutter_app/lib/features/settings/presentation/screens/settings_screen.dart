import 'package:flutter/material.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';

class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return AppScaffold(
      title: 'Settings',
      currentRoute: AppRoutes.settings,
      subtitle: 'Placeholder configuration panels for notifications, security, and workspace preferences.',
      body: Column(
        children: <Widget>[
          Card(
            child: Column(
              children: <Widget>[
                const ListTile(
                  leading: Icon(Icons.notifications_outlined),
                  title: Text('Notifications'),
                  subtitle: Text('Receive alerts for warnings and critical issues'),
                ),
                const Divider(height: 1),
                const ListTile(
                  leading: Icon(Icons.security_outlined),
                  title: Text('Security'),
                  subtitle: Text('Manage sign-in and remote access preferences'),
                ),
                const Divider(height: 1),
                const ListTile(
                  leading: Icon(Icons.palette_outlined),
                  title: Text('Appearance'),
                  subtitle: Text('Professional light theme, spacing, and density'),
                ),
                const Divider(height: 1),
                SwitchListTile(
                  value: true,
                  onChanged: (_) {},
                  secondary: const Icon(Icons.sync_rounded),
                  title: const Text('Background Sync'),
                  subtitle: const Text('Keep mock device status refreshed automatically'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
