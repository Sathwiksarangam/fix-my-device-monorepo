import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
import '../../../../core/widgets/device_detail_row.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../../core/widgets/status_chip.dart';
import '../../data/models/device.dart';
import '../../data/services/api_device_service.dart';
import '../../../files/data/models/recovery_models.dart';

class DeviceDetailsScreen extends StatelessWidget {
  const DeviceDetailsScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  Widget build(BuildContext context) {
    return AppScaffold(
      title: 'Device Details',
      currentRoute: AppRoutes.devices,
      subtitle:
          'Review live system metrics, storage details, and connected drive information.',
      body: FutureBuilder<List<dynamic>>(
        future: ApiDeviceService().getDevices(),
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }

          if (snapshot.hasError) {
            return Card(
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Text('Error: ${snapshot.error}'),
              ),
            );
          }

          final devices = snapshot.data ?? [];
          final device = devices.firstWhere(
            (d) =>
                '${d['id'] ?? d['deviceId'] ?? ''}' ==
                (deviceId ?? ''),
            orElse: () => devices.isNotEmpty ? devices.first : null,
          );

          if (device == null) {
            return const Center(child: Text('No device found'));
          }

          final deviceName = '${device['deviceName'] ?? 'Unknown'}';
          final status = '${device['status'] ?? 'Online'}';
          final lastSeen = '${device['lastSeenAt'] ?? ''}';

          final deviceStatus = status.toLowerCase() == 'online'
              ? DeviceStatus.healthy
              : DeviceStatus.offline;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      '${device['windowsEdition']} ${device['windowsVersion']}',
                      style: Theme.of(context)
                          .textTheme
                          .titleMedium
                          ?.copyWith(color: Colors.black54),
                    ),
                  ),
                  StatusChip(status: deviceStatus),
                ],
              ),

              const SizedBox(height: 18),

              Wrap(
                spacing: 16,
                runSpacing: 16,
                children: [
                  InfoCard(
                    title: 'Device',
                    value: deviceName,
                    subtitle: 'Last seen $lastSeen',
                    icon: Icons.computer,
                  ),
                  InfoCard(
                    title: 'Storage',
                    value: device['totalStorage'],
                    subtitle: 'Used: ${device['usedStorage']}',
                    icon: Icons.sd_storage,
                  ),
                  InfoCard(
                    title: 'Graphics',
                    value: device['graphicsCard'],
                    subtitle: 'Memory: ${device['graphicsMemory']}',
                    icon: Icons.memory,
                  ),
                ],
              ),

              const SizedBox(height: 24),

              const Text(
                'System Details',
                style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold),
              ),

              const SizedBox(height: 12),

              Card(
                child: Padding(
                  padding: const EdgeInsets.all(18),
                  child: Column(
                    children: [
                      DeviceDetailRow(label: 'Device ID', value: '${device['deviceId']}'),
                      DeviceDetailRow(label: 'Product ID', value: '${device['productId']}'),

                      DeviceDetailRow(label: 'Processor', value: '${device['processor']}'),
                      DeviceDetailRow(label: 'Speed', value: '${device['processorSpeed']}'),

                      DeviceDetailRow(label: 'Installed RAM', value: '${device['installedRam']}'),
                      DeviceDetailRow(label: 'Usable RAM', value: '${device['usableRam']}'),

                      DeviceDetailRow(label: 'Graphics', value: '${device['graphicsCard']}'),
                      DeviceDetailRow(label: 'Graphics Memory', value: '${device['graphicsMemory']}'),

                      DeviceDetailRow(label: 'Total Storage', value: '${device['totalStorage']}'),
                      DeviceDetailRow(label: 'Used Storage', value: '${device['usedStorage']}'),
                      DeviceDetailRow(label: 'Free Storage', value: '${device['freeStorage']}'),

                      DeviceDetailRow(label: 'System Type', value: '${device['systemType']}'),

                      DeviceDetailRow(label: 'Windows Edition', value: '${device['windowsEdition']}'),
                      DeviceDetailRow(label: 'Version', value: '${device['windowsVersion']}'),
                      DeviceDetailRow(label: 'OS Build', value: '${device['osBuild']}'),
                      DeviceDetailRow(label: 'Installed On', value: '${device['installedOn']}'),

                      DeviceDetailRow(label: 'Last Seen', value: lastSeen),
                    ],
                  ),
                ),
              ),

              const SizedBox(height: 20),

              const Text(
                'Drives',
                style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold),
              ),

              const SizedBox(height: 12),

              Card(
                child: Padding(
                  padding: const EdgeInsets.all(18),
                  child: Column(
                    children: (device['drives'] as List? ?? [])
                        .map<Widget>((d) => DeviceDetailRow(
                              label: d['driveLetter'],
                              value:
                                  'Total: ${d['totalSize']} | Used: ${d['usedSpace']} | Free: ${d['freeSpace']}',
                            ))
                        .toList(),
                  ),
                ),
              ),

              const SizedBox(height: 20),

              const Text(
                'Emergency Recovery',
                style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold),
              ),

              const SizedBox(height: 12),

              FutureBuilder<List<dynamic>>(
                future: Future.wait<dynamic>([
                  ApiDeviceService().getRecoverySettings('${device['id']}'),
                  ApiDeviceService().getRecoveryFiles('${device['id']}'),
                ]),
                builder: (context, recoverySnapshot) {
                  final bool isLoadingRecovery =
                      recoverySnapshot.connectionState == ConnectionState.waiting;
                  final bool hasRecoveryError = recoverySnapshot.hasError;
                  final RecoverySettings? settings = recoverySnapshot.hasData
                      ? recoverySnapshot.data![0] as RecoverySettings
                      : null;
                  final List<RecoveryFileEntry> files = recoverySnapshot.hasData
                      ? recoverySnapshot.data![1] as List<RecoveryFileEntry>
                      : <RecoveryFileEntry>[];
                  final String lastScanTime =
                      (settings?.lastSyncedAt ?? '').isNotEmpty
                          ? settings!.lastSyncedAt
                          : 'Not scanned yet';
                  final String totalFiles = files.length.toString();
                  final String recoveryStatus =
                      settings?.enabled == true ? 'Enabled' : 'Not Enabled';
                  final bool showSetupHint =
                      settings != null &&
                      !settings.enabled &&
                      files.isEmpty &&
                      settings.lastSyncedAt.isEmpty;

                  return Card(
                    child: Padding(
                      padding: const EdgeInsets.all(18),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          if (isLoadingRecovery)
                            const Padding(
                              padding: EdgeInsets.symmetric(vertical: 12),
                              child: Center(child: CircularProgressIndicator()),
                            )
                          else if (hasRecoveryError)
                            Text(
                              'Could not load Emergency Recovery right now.\n${recoverySnapshot.error}',
                            )
                          else ...[
                            DeviceDetailRow(
                              label: 'Emergency Recovery',
                              value: recoveryStatus,
                            ),
                            DeviceDetailRow(
                              label: 'Total Files',
                              value: totalFiles,
                            ),
                            DeviceDetailRow(
                              label: 'Last Scan Time',
                              value: lastScanTime,
                            ),
                            if (showSetupHint) ...[
                              const SizedBox(height: 8),
                              const Text(
                                'Emergency Recovery is not enabled yet. Open the Fix My Device Agent and choose Emergency Recovery setup.',
                              ),
                            ],
                          ],
                          const SizedBox(height: 14),
                          Wrap(
                            spacing: 12,
                            runSpacing: 12,
                            children: [
                              ActionButton(
                                label: 'Browse Recovery Files',
                                icon: Icons.folder_open_rounded,
                                onPressed: () => context.go(
                                  '${AppRoutes.emergencyRecovery}?id=${device['id']}',
                                ),
                              ),
                              ActionButton(
                                label: 'Configure Recovery',
                                icon: Icons.health_and_safety_rounded,
                                onPressed: () => context.go(
                                  '${AppRoutes.emergencyRecovery}?id=${device['id']}',
                                ),
                                isPrimary: false,
                              ),
                            ],
                          ),
                        ],
                      ),
                    ),
                  );
                },
              ),

              const SizedBox(height: 20),

              Wrap(
                spacing: 12,
                runSpacing: 12,
                children: [
                  ActionButton(
                    label: 'Transfer Files',
                    icon: Icons.upload_file,
                    onPressed: () => context.go(
                        '${AppRoutes.fileTransfer}?id=${device['id']}'),
                  ),
                  ActionButton(
                    label: 'Troubleshoot',
                    icon: Icons.build,
                    onPressed: () => context.go(
                        '${AppRoutes.troubleshooting}?id=${device['id']}'),
                    isPrimary: false,
                  ),
                ],
              ),
            ],
          );
        },
      ),
    );
  }
}
