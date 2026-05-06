import 'package:flutter/material.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../devices/data/services/mock_device_service.dart';

class TroubleshootingScreen extends StatelessWidget {
  const TroubleshootingScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  Widget build(BuildContext context) {
    final service = MockDeviceService();
    final device = service.getDeviceById(deviceId);
    final actions = service.getSuggestedActions(deviceId);

    return AppScaffold(
      title: 'Troubleshooting',
      currentRoute: AppRoutes.troubleshooting,
      subtitle: 'Use mock remediation steps to guide diagnostics and escalation paths.',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          InfoCard(
            title: 'Recommended Workflow',
            value: device.name,
            subtitle: device.notes,
            icon: Icons.medical_services_outlined,
          ),
          const SizedBox(height: 20),
          ...actions.asMap().entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: Card(
                child: ListTile(
                  leading: CircleAvatar(
                    backgroundColor:
                        Theme.of(context).colorScheme.primaryContainer,
                    child: Text('${entry.key + 1}'),
                  ),
                  title: Text(entry.value),
                  subtitle: const Text('Mock diagnostic recommendation'),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
