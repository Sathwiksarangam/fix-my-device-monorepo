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

  Future<void> _copyText(String text, String message) async {
    await Clipboard.setData(ClipboardData(text: text));

    if (!mounted) {
      return;
    }

    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message)),
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

  Widget _buildReconnectHelp(String setupCode) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Row(
              children: <Widget>[
                const Icon(Icons.settings_backup_restore_rounded, size: 22),
                const SizedBox(width: 10),
                Text(
                  'Reconnect Agent',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            const Text(
              'If this computer was connected to another account before, reset the agent and enter this setup code again.',
            ),
            const SizedBox(height: 16),
            InfoCard(
              title: 'Agent Setup Code',
              value: setupCode,
              subtitle:
                  'Copy this code, open the Fix My Device Agent, choose "Enter new setup code", and paste it there.',
              icon: Icons.key_rounded,
              trailing: IconButton(
                onPressed: () => _copyText(
                  setupCode,
                  'Agent setup code copied.',
                ),
                icon: const Icon(Icons.copy_rounded),
                tooltip: 'Copy setup code',
              ),
            ),
            const SizedBox(height: 16),
            Text(
              'Reset saved setup code',
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
            ),
            const SizedBox(height: 8),
            const Text(
              'Run this PowerShell command on the Windows computer if the agent keeps reconnecting to an older account.',
            ),
            const SizedBox(height: 12),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: const Color(0xFFF6F8FB),
                borderRadius: BorderRadius.circular(14),
                border: Border.all(color: const Color(0xFFD8E0EA)),
              ),
              child: SelectableText(
                ApiDeviceService.resetAgentCommand,
                style: const TextStyle(
                  fontFamily: 'Consolas',
                  fontSize: 13,
                  height: 1.45,
                ),
              ),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: <Widget>[
                ActionButton(
                  label: 'Copy Reset Command',
                  icon: Icons.content_copy_rounded,
                  onPressed: () => _copyText(
                    ApiDeviceService.resetAgentCommand,
                    'PowerShell reset command copied.',
                  ),
                  isPrimary: false,
                ),
                ActionButton(
                  label: 'Copy Setup Code',
                  icon: Icons.key_rounded,
                  onPressed: () => _copyText(
                    setupCode,
                    'Agent setup code copied.',
                  ),
                  isPrimary: false,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildDeviceCard(dynamic device) {
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
              children: <Widget>[
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
          onTap: () => context.go('${AppRoutes.deviceDetails}?id=$deviceId'),
        ),
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
        builder: (
          BuildContext context,
          AsyncSnapshot<_DevicesPageData> snapshot,
        ) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }

          if (snapshot.hasError) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: <Widget>[
                        const Text(
                          'We could not load your devices',
                          style: TextStyle(
                            fontSize: 20,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                        const SizedBox(height: 10),
                        Text(
                          snapshot.error.toString(),
                          style: Theme.of(context).textTheme.bodyMedium,
                        ),
                        const SizedBox(height: 16),
                        Wrap(
                          spacing: 12,
                          runSpacing: 12,
                          children: <Widget>[
                            ActionButton(
                              label: 'Refresh Devices',
                              icon: Icons.refresh_rounded,
                              onPressed: _refreshDevices,
                            ),
                            ActionButton(
                              label: 'Download Agent',
                              icon: Icons.download_rounded,
                              onPressed: _downloadAgent,
                              isPrimary: false,
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            );
          }

          final _DevicesPageData pageData = snapshot.data ??
              const _DevicesPageData(devices: <dynamic>[], agentSetupCode: '');
          final List<dynamic> devices = pageData.devices;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              _buildReconnectHelp(pageData.agentSetupCode),
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
                    label: 'Refresh Devices',
                    icon: Icons.refresh_rounded,
                    onPressed: _refreshDevices,
                    isPrimary: false,
                  ),
                ],
              ),
              const SizedBox(height: 16),
              if (devices.isEmpty) ...<Widget>[
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
                          'Download and install the Fix My Device Agent to connect this computer, or reset an older saved setup code and reconnect with the code above.',
                        ),
                      ],
                    ),
                  ),
                ),
              ] else ...<Widget>[
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        const Text(
                          'Connected devices',
                          style: TextStyle(
                            fontSize: 20,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                        const SizedBox(height: 10),
                        Text(
                          'Refresh Devices calls the backend again and updates this list as soon as your agent posts the latest system info.',
                          style: Theme.of(context).textTheme.bodyMedium,
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 12),
                ...devices.map(_buildDeviceCard),
              ],
            ],
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
