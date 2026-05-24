import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../devices/data/services/mock_device_service.dart';

class DashboardScreen extends StatelessWidget {
  const DashboardScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final MockDeviceService service = MockDeviceService();
    final devices = service.getDevices();
    final activeDevices = devices.where((device) => device.isOnline).length;
    final alerts = devices.where((device) => device.needsAttention).length;

    return AppScaffold(
      title: 'Dashboard',
      currentRoute: AppRoutes.dashboard,
      subtitle: 'Track connected devices, recovery readiness, and support actions from one place.',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Wrap(
            spacing: 16,
            runSpacing: 16,
            children: <Widget>[
              SizedBox(
                width: 280,
                child: InfoCard(
                  title: 'Managed Devices',
                  value: '${devices.length}',
                  subtitle: 'Across field and office locations',
                  icon: Icons.devices_rounded,
                ),
              ),
              SizedBox(
                width: 280,
                child: InfoCard(
                  title: 'Online Now',
                  value: '$activeDevices',
                  subtitle: 'Connected and available',
                  icon: Icons.wifi_tethering_rounded,
                ),
              ),
              SizedBox(
                width: 280,
                child: InfoCard(
                  title: 'Needs Attention',
                  value: '$alerts',
                  subtitle: 'Warnings and critical issues',
                  icon: Icons.warning_amber_rounded,
                ),
              ),
            ],
          ),
          const SizedBox(height: 24),
          const Text(
            'Quick Actions',
            style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 12,
            runSpacing: 12,
            children: <Widget>[
              _DashboardAction(
                label: 'Devices',
                icon: Icons.storage_rounded,
                onTap: () => context.go(AppRoutes.devices),
              ),
              _DashboardAction(
                label: 'Emergency Recovery',
                icon: Icons.health_and_safety_rounded,
                onTap: () => context.go(AppRoutes.emergencyRecovery),
              ),
              _DashboardAction(
                label: 'Transfers',
                icon: Icons.swap_horiz_rounded,
                onTap: () => context.go(AppRoutes.fileTransfer),
              ),
              _DashboardAction(
                label: 'Troubleshooting',
                icon: Icons.build_circle_outlined,
                onTap: () => context.go(AppRoutes.troubleshooting),
              ),
              _DashboardAction(
                label: 'Settings',
                icon: Icons.settings_outlined,
                onTap: () => context.go(AppRoutes.settings),
              ),
            ],
          ),
          const SizedBox(height: 24),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  const Text(
                    'Recent Attention Items',
                    style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
                  ),
                  const SizedBox(height: 12),
                  ...devices
                      .where((device) => device.needsAttention)
                      .map(
                        (device) => ListTile(
                          contentPadding: EdgeInsets.zero,
                          leading: Icon(
                            Icons.priority_high_rounded,
                            color: Theme.of(context).colorScheme.primary,
                          ),
                          title: Text(device.name),
                          subtitle: Text(device.notes),
                          trailing: Text(device.lastSeen),
                        ),
                      ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _DashboardAction extends StatelessWidget {
  const _DashboardAction({
    required this.label,
    required this.icon,
    required this.onTap,
  });

  final String label;
  final IconData icon;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(20),
      child: Ink(
        width: 172,
        padding: const EdgeInsets.all(18),
        decoration: BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: Colors.black.withOpacity(0.06)),
          boxShadow: const <BoxShadow>[
            BoxShadow(
              color: Color(0x12000000),
              blurRadius: 24,
              offset: Offset(0, 10),
            ),
          ],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Icon(icon, color: Theme.of(context).colorScheme.primary),
            const SizedBox(height: 16),
            Text(
              label,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w700,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
