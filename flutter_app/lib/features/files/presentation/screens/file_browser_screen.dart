import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../devices/data/services/api_device_service.dart';
import '../../data/models/recovery_models.dart';

class FileBrowserScreen extends StatefulWidget {
  const FileBrowserScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  State<FileBrowserScreen> createState() => _FileBrowserScreenState();
}

class _FileBrowserScreenState extends State<FileBrowserScreen> {
  late Future<_EmergencyRecoveryPageData> _pageFuture;
  String? _selectedDeviceId;

  @override
  void initState() {
    super.initState();
    _selectedDeviceId = widget.deviceId;
    _pageFuture = _loadPageData();
  }

  Future<_EmergencyRecoveryPageData> _loadPageData() async {
    final api = ApiDeviceService();
    final devices = await api.getDevices();

    if (devices.isEmpty) {
      return const _EmergencyRecoveryPageData(
        devices: <dynamic>[],
        selectedDeviceId: null,
        settings: null,
        files: <RecoveryFileEntry>[],
      );
    }

    final selectedDevice = devices.firstWhere(
      (dynamic device) => '${device['id'] ?? ''}' == (_selectedDeviceId ?? ''),
      orElse: () => devices.first,
    );

    final resolvedDeviceId = '${selectedDevice['id'] ?? ''}';
    _selectedDeviceId = resolvedDeviceId;

    final results = await Future.wait<dynamic>(<Future<dynamic>>[
      api.getRecoverySettings(resolvedDeviceId),
      api.getRecoveryFileList(resolvedDeviceId),
    ]);

    return _EmergencyRecoveryPageData(
      devices: devices,
      selectedDeviceId: resolvedDeviceId,
      settings: results[0] as RecoverySettings,
      files: results[1] as List<RecoveryFileEntry>,
    );
  }

  void _refresh() {
    if (!mounted) {
      return;
    }

    setState(() {
      _pageFuture = _loadPageData();
    });
  }

  String _formatBytes(int bytes) {
    const units = <String>['B', 'KB', 'MB', 'GB', 'TB'];
    double size = bytes.toDouble();
    var unitIndex = 0;

    while (size >= 1024 && unitIndex < units.length - 1) {
      size /= 1024;
      unitIndex++;
    }

    return '${size.toStringAsFixed(size >= 100 || unitIndex == 0 ? 0 : 1)} ${units[unitIndex]}';
  }

  Widget _buildStatusCard(
    BuildContext context,
    RecoverySettings? settings,
  ) {
    final enabled = settings?.enabled == true;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Row(
              children: <Widget>[
                Icon(
                  enabled
                      ? Icons.verified_user_rounded
                      : Icons.warning_amber_rounded,
                  color: enabled
                      ? Theme.of(context).colorScheme.primary
                      : const Color(0xFF9A6700),
                ),
                const SizedBox(width: 12),
                Text(
                  enabled ? 'Enabled' : 'Not Enabled',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            const Text(
              'Enable this before a screen failure happens. File transfer will be added next.',
            ),
            if ((settings?.lastSyncedAt ?? '').isNotEmpty) ...<Widget>[
              const SizedBox(height: 10),
              Text(
                'Last synced: ${settings!.lastSyncedAt}',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: Colors.black54,
                    ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildApprovedLocationsCard(
    BuildContext context,
    RecoverySettings? settings,
  ) {
    final approvedLocations = settings?.approvedLocations ?? <RecoveryApprovedLocation>[];

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Text(
              'Approved Recovery Locations',
              style: Theme.of(context).textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w800,
                  ),
            ),
            const SizedBox(height: 10),
            if (approvedLocations.isEmpty)
              const Text(
                'Emergency Recovery Mode has not been enabled on this computer yet.',
              )
            else
              Wrap(
                spacing: 12,
                runSpacing: 12,
                children: approvedLocations
                    .map(
                      (location) => SizedBox(
                        width: 260,
                        child: InfoCard(
                          title: location.label,
                          value: location.driveLetter.isEmpty
                              ? location.locationType
                              : location.driveLetter,
                          subtitle: location.fullPath,
                          icon: location.locationType == 'Drive'
                              ? Icons.storage_rounded
                              : Icons.folder_open_rounded,
                        ),
                      ),
                    )
                    .toList(),
              ),
          ],
        ),
      ),
    );
  }

  Widget _buildFileListCard(
    BuildContext context,
    List<RecoveryFileEntry> files,
  ) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Row(
              children: <Widget>[
                Expanded(
                  child: Text(
                    'Recovery File Listing',
                    style: Theme.of(context).textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w800,
                        ),
                  ),
                ),
                ActionButton(
                  label: 'Refresh File List',
                  icon: Icons.refresh_rounded,
                  onPressed: _refresh,
                  isPrimary: false,
                ),
              ],
            ),
            const SizedBox(height: 10),
            if (files.isEmpty)
              const Text(
                'No recovery files are available yet. Enable Emergency Recovery Mode in the Windows agent, then refresh this page.',
              )
            else ...<Widget>[
              Text(
                '${files.length} files and folders are ready for phase-two recovery transfer.',
                style: Theme.of(context).textTheme.bodyMedium,
              ),
              const SizedBox(height: 16),
              ...files.take(250).map(
                    (file) => Padding(
                      padding: const EdgeInsets.only(bottom: 10),
                      child: ListTile(
                        contentPadding: EdgeInsets.zero,
                        leading: CircleAvatar(
                          backgroundColor:
                              Theme.of(context).colorScheme.primaryContainer,
                          child: Icon(
                            file.isDirectory
                                ? Icons.folder_rounded
                                : Icons.insert_drive_file_rounded,
                            color: Theme.of(context).colorScheme.primary,
                          ),
                        ),
                        title: Text(file.fileName),
                        subtitle: Text(
                          '${file.fullPath}\n${file.driveLetter} • ${file.isDirectory ? 'Folder' : _formatBytes(file.sizeBytes)}',
                        ),
                        isThreeLine: true,
                        trailing: Text(
                          file.lastModified.isEmpty ? 'Unknown' : file.lastModified,
                          textAlign: TextAlign.end,
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: Colors.black54,
                              ),
                        ),
                      ),
                    ),
                  ),
            ],
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return AppScaffold(
      title: 'Emergency Recovery',
      currentRoute: AppRoutes.emergencyRecovery,
      subtitle:
          'Prepare safe, metadata-only file recovery before a laptop screen fails.',
      body: FutureBuilder<_EmergencyRecoveryPageData>(
        future: _pageFuture,
        builder: (
          BuildContext context,
          AsyncSnapshot<_EmergencyRecoveryPageData> snapshot,
        ) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }

          if (snapshot.hasError) {
            return Card(
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    const Text(
                      'We could not load Emergency Recovery',
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: 10),
                    Text(snapshot.error.toString()),
                    const SizedBox(height: 16),
                    ActionButton(
                      label: 'Try Again',
                      icon: Icons.refresh_rounded,
                      onPressed: _refresh,
                    ),
                  ],
                ),
              ),
            );
          }

          final pageData = snapshot.data ??
              const _EmergencyRecoveryPageData(
                devices: <dynamic>[],
                selectedDeviceId: null,
                settings: null,
                files: <RecoveryFileEntry>[],
              );

          if (pageData.devices.isEmpty) {
            return Card(
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    const Text(
                      'No connected device found',
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: 10),
                    const Text(
                      'Connect a Windows device first. Emergency Recovery can only be prepared after the agent is installed and linked to your account.',
                    ),
                    const SizedBox(height: 16),
                    ActionButton(
                      label: 'Go To Devices',
                      icon: Icons.devices_other_rounded,
                      onPressed: () => context.go(AppRoutes.devices),
                    ),
                  ],
                ),
              ),
            );
          }

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Text(
                        'Choose Device',
                        style: Theme.of(context).textTheme.titleLarge?.copyWith(
                              fontWeight: FontWeight.w800,
                            ),
                      ),
                      const SizedBox(height: 12),
                      DropdownButtonFormField<String>(
                        initialValue: pageData.selectedDeviceId,
                        decoration: const InputDecoration(
                          labelText: 'Connected device',
                          border: OutlineInputBorder(),
                        ),
                        items: pageData.devices
                            .map(
                              (dynamic device) => DropdownMenuItem<String>(
                                value: '${device['id'] ?? ''}',
                                child: Text('${device['deviceName'] ?? 'Unknown Device'}'),
                              ),
                            )
                            .toList(),
                        onChanged: (String? value) {
                          if (value == null || value == _selectedDeviceId) {
                            return;
                          }

                          setState(() {
                            _selectedDeviceId = value;
                            _pageFuture = _loadPageData();
                          });
                        },
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 16),
              _buildStatusCard(context, pageData.settings),
              const SizedBox(height: 16),
              _buildApprovedLocationsCard(context, pageData.settings),
              const SizedBox(height: 16),
              _buildFileListCard(context, pageData.files),
            ],
          );
        },
      ),
    );
  }
}

class _EmergencyRecoveryPageData {
  const _EmergencyRecoveryPageData({
    required this.devices,
    required this.selectedDeviceId,
    required this.settings,
    required this.files,
  });

  final List<dynamic> devices;
  final String? selectedDeviceId;
  final RecoverySettings? settings;
  final List<RecoveryFileEntry> files;
}
