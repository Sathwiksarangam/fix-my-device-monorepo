import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../../core/widgets/status_chip.dart';
import '../../../auth/data/auth_service.dart';
import '../../data/models/device.dart';
import '../../data/services/api_device_service.dart';

class DevicesListScreen extends StatefulWidget {
  const DevicesListScreen({super.key});

  @override
  State<DevicesListScreen> createState() => _DevicesListScreenState();
}

class _DevicesListScreenState extends State<DevicesListScreen> {
  late Future<_DevicesPageData> _devicesFuture;

  @override
  void initState() {
    super.initState();
    _devicesFuture = _loadDevicesPageData();
    AuthService.authState.addListener(_refreshDevices);
  }

  @override
  void dispose() {
    AuthService.authState.removeListener(_refreshDevices);
    super.dispose();
  }

  void _refreshDevices() {
    if (!mounted) {
      return;
    }

    setState(() {
      _devicesFuture = _loadDevicesPageData();
    });
  }

  Future<_DevicesPageData> _loadDevicesPageData() async {
    final apiDeviceService = ApiDeviceService();
    final results = await Future.wait<dynamic>(<Future<dynamic>>[
      apiDeviceService.getDevices(),
      apiDeviceService.getAgentSetupCode(),
    ]);

    return _DevicesPageData(
      devices: results[0] as List<dynamic>,
      agentSetupCode: results[1] as String,
    );
  }

  Future<void> _copySetupCode(String setupCode) async {
    await Clipboard.setData(ClipboardData(text: setupCode));

    if (!mounted) {
      return;
    }

    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Agent setup code copied.')),
    );
  }

  Future<void> _downloadAgent() async {
    final bool launched = await launchUrl(
      Uri.parse(ApiDeviceService.agentDownloadUrl),
      webOnlyWindowName: '_self',
    );

    if (launched || !mounted) {
      return;
    }

    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text('Could not open ${ApiDeviceService.agentDownloadUrl}'),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return AppScaffold(
      title: 'Devices',
      currentRoute: AppRoutes.devices,
      subtitle: 'Browse connected Windows devices and view their system details.',
      body: FutureBuilder<_DevicesPageData>(
        future: _devicesFuture,
        builder:
            (BuildContext context, AsyncSnapshot<_DevicesPageData> snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }

          if (snapshot.hasError) {
            return Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Text('Could not load devices: ${snapshot.error}'),
              ),
            );
          }

          final _DevicesPageData pageData = snapshot.data ??
              const _DevicesPageData(devices: <dynamic>[], agentSetupCode: '');
          final List<dynamic> devices = pageData.devices;

          if (devices.isEmpty) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: const <Widget>[
                        Text(
                          'No device connected yet',
                          style: TextStyle(
                            fontSize: 20,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                        SizedBox(height: 10),
                        Text(
                          'Download and install the Fix My Device Agent to connect this computer.',
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 16),
                InfoCard(
                  title: 'Agent Setup Code',
                  value: pageData.agentSetupCode,
                  subtitle:
                      'Use this one-time code during the first Fix My Device Agent setup on this computer.',
                  icon: Icons.key_rounded,
                  trailing: IconButton(
                    onPressed: () => _copySetupCode(pageData.agentSetupCode),
                    icon: const Icon(Icons.copy_rounded),
                    tooltip: 'Copy setup code',
                  ),
                ),
                const SizedBox(height: 16),
                Wrap(
                  spacing: 12,
                  runSpacing: 12,
                  children: <Widget>[
                    ActionButton(
                      label: 'Download Agent',
                      icon: Icons.download_rounded,
                      onPressed: _downloadAgent,
                    ),
                    ActionButton(
                      label: 'Connect Device',
                      icon: Icons.copy_rounded,
                      onPressed: () => _copySetupCode(pageData.agentSetupCode),
                      isPrimary: false,
                    ),
                    ActionButton(
                      label: 'Refresh Devices',
                      icon: Icons.refresh_rounded,
                      onPressed: _refreshDevices,
                      isPrimary: false,
                    ),
                  ],
                ),
              ],
            );
          }

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: devices.map((device) {
              final deviceName = device['deviceName'] ?? 'Unknown Device';
              final systemType = device['systemType'] ?? 'Windows';
              final lastSeen = device['lastSeenAt'] ?? 'Not available';
              final status = device['status'] ?? 'Online';
              final deviceId = device['id'] ?? '';

              final processor = device['processor'] ?? 'Processor not available';
              final installedRam = device['installedRam'] ?? 'RAM not available';
              final totalStorage = device['totalStorage'] ?? 'Storage not available';
              final windowsVersion = device['windowsVersion'] ?? systemType;

              return Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Card(
                  child: ListTile(
                    contentPadding: const EdgeInsets.all(16),
                    leading: CircleAvatar(
                      backgroundColor: Theme.of(context).colorScheme.primaryContainer,
                      child: Icon(
                        Icons.laptop_windows_rounded,
                        color: Theme.of(context).colorScheme.primary,
                      ),
                    ),
                    title: Text(
                      deviceName,
                      style: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                    subtitle: Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text(
                        '$windowsVersion\n$processor\nRAM: $installedRam • Storage: $totalStorage',
                      ),
                    ),
                    trailing: SizedBox(
                      width: 130,
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          StatusChip(
                            status: status.toString().toLowerCase() == 'online'
                                ? DeviceStatus.healthy
                                : DeviceStatus.offline,
                          ),
                          const SizedBox(height: 4),
                          Text(
                            lastSeen.toString(),
                            overflow: TextOverflow.ellipsis,
                            maxLines: 1,
                            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                  color: Colors.black54,
                                ),
                          ),
                        ],
                      ),
                    ),
                    onTap: () => context.go(
                      '${AppRoutes.deviceDetails}?id=$deviceId',
                    ),
                  ),
                ),
              );
            }).toList(),
          );
        },
      ),
    );
  }
}

class _DevicesPageData {
  const _DevicesPageData({
    required this.devices,
    required this.agentSetupCode,
  });

  final List<dynamic> devices;
  final String agentSetupCode;
}
