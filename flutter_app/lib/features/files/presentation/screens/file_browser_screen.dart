import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
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
  static const List<RecoveryApprovedLocation> _defaultUserFolders =
      <RecoveryApprovedLocation>[
    RecoveryApprovedLocation(
      label: 'Desktop',
      fullPath: '%FMD_DESKTOP%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
    RecoveryApprovedLocation(
      label: 'Documents',
      fullPath: '%FMD_DOCUMENTS%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
    RecoveryApprovedLocation(
      label: 'Downloads',
      fullPath: '%FMD_DOWNLOADS%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
    RecoveryApprovedLocation(
      label: 'Pictures',
      fullPath: '%FMD_PICTURES%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
    RecoveryApprovedLocation(
      label: 'Videos',
      fullPath: '%FMD_VIDEOS%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
    RecoveryApprovedLocation(
      label: 'Music',
      fullPath: '%FMD_MUSIC%',
      driveLetter: 'C:',
      locationType: 'UserFolder',
    ),
  ];

  late Future<_EmergencyRecoveryPageData> _pageFuture;
  final TextEditingController _searchController = TextEditingController();

  String? _selectedDeviceId;
  String? _draftDeviceId;
  String _draftDeviceName = '';
  bool _isSaving = false;
  bool _isRequestingScan = false;
  bool _isQueuingDownloads = false;
  List<RecoveryApprovedLocation> _draftLocations = <RecoveryApprovedLocation>[];
  Map<String, bool> _draftSelections = <String, bool>{};
  String? _currentRootKey;
  String? _currentFolderKey;
  final Set<String> _checkedPaths = <String>{};

  @override
  void initState() {
    super.initState();
    _selectedDeviceId = widget.deviceId;
    _searchController.addListener(_handleSearchChanged);
    _pageFuture = _loadPageData();
  }

  @override
  void dispose() {
    _searchController
      ..removeListener(_handleSearchChanged)
      ..dispose();
    super.dispose();
  }

  void _handleSearchChanged() {
    if (mounted) {
      setState(() {});
    }
  }

  Future<_EmergencyRecoveryPageData> _loadPageData() async {
    final ApiDeviceService api = ApiDeviceService();
    final List<dynamic> devices = await api.getDevices();

    if (devices.isEmpty) {
      return const _EmergencyRecoveryPageData(
        devices: <dynamic>[],
        selectedDeviceId: null,
        settings: null,
        files: <RecoveryFileEntry>[],
        selectableLocations: <RecoveryApprovedLocation>[],
      );
    }

    final dynamic selectedDevice = devices.firstWhere(
      (dynamic device) => '${device['id'] ?? ''}' == (_selectedDeviceId ?? ''),
      orElse: () => devices.first,
    );

    final String resolvedDeviceId = '${selectedDevice['id'] ?? ''}';
    final String resolvedDeviceName =
        '${selectedDevice['deviceName'] ?? 'Unknown Device'}';
    _selectedDeviceId = resolvedDeviceId;

    final List<dynamic> results = await Future.wait<dynamic>(<Future<dynamic>>[
      api.getRecoverySettings(resolvedDeviceId),
      api.getRecoveryFileList(resolvedDeviceId),
    ]);

    final RecoverySettings settings = results[0] as RecoverySettings;
    final List<RecoveryFileEntry> files = results[1] as List<RecoveryFileEntry>;
    final List<RecoveryApprovedLocation> selectableLocations =
        _buildSelectableLocations(selectedDevice, settings);
    final _RecoveryExplorerModel explorer =
        _RecoveryExplorerModel.fromEntries(files, settings.approvedLocations);

    if (_draftDeviceId != resolvedDeviceId) {
      _draftDeviceId = resolvedDeviceId;
      _draftDeviceName = resolvedDeviceName;
      _draftLocations = selectableLocations;
      _draftSelections = <String, bool>{
        for (final RecoveryApprovedLocation location in selectableLocations)
          location.fullPath: _isLocationSelected(location, settings),
      };
      _checkedPaths.clear();
      _currentRootKey =
          explorer.roots.isNotEmpty ? explorer.roots.first.fullPath : null;
      _currentFolderKey = _currentRootKey;
      _searchController.clear();
    } else {
      if (_currentRootKey == null ||
          !explorer.nodesByPath.containsKey(_currentRootKey)) {
        _currentRootKey =
            explorer.roots.isNotEmpty ? explorer.roots.first.fullPath : null;
      }

      if (_currentFolderKey == null ||
          !explorer.nodesByPath.containsKey(_currentFolderKey)) {
        _currentFolderKey = _currentRootKey;
      }
    }

    return _EmergencyRecoveryPageData(
      devices: devices,
      selectedDeviceId: resolvedDeviceId,
      settings: settings,
      files: files,
      selectableLocations: selectableLocations,
    );
  }

  List<RecoveryApprovedLocation> _buildSelectableLocations(
    dynamic device,
    RecoverySettings settings,
  ) {
    final List<RecoveryApprovedLocation> merged = <RecoveryApprovedLocation>[];
    final Set<String> seenPaths = <String>{};

    void addLocation(RecoveryApprovedLocation location) {
      if (seenPaths.add(location.fullPath)) {
        merged.add(location);
      }
    }

    for (final RecoveryApprovedLocation location in _defaultUserFolders) {
      addLocation(location);
    }

    final List<dynamic> drives = (device['drives'] as List<dynamic>? ?? <dynamic>[]);
    for (final dynamic drive in drives) {
      final String driveLetter = '${drive['driveLetter'] ?? ''}'.trim();
      final String driveType = '${drive['driveType'] ?? ''}'.trim();

      if (driveLetter.isEmpty ||
          driveLetter.toUpperCase().startsWith('C:') ||
          (driveType.isNotEmpty &&
              driveType != 'Fixed' &&
              driveType != 'Removable')) {
        continue;
      }

      final String normalizedDriveLetter =
          driveLetter.endsWith(r'\')
              ? driveLetter.substring(0, driveLetter.length - 1)
              : driveLetter;

      addLocation(
        RecoveryApprovedLocation(
          label: normalizedDriveLetter,
          fullPath: '$normalizedDriveLetter\\',
          driveLetter: normalizedDriveLetter,
          locationType: 'Drive',
        ),
      );
    }

    for (final RecoveryApprovedLocation location in settings.approvedLocations) {
      addLocation(location);
    }

    return merged;
  }

  bool _isLocationSelected(
    RecoveryApprovedLocation location,
    RecoverySettings settings,
  ) {
    if (settings.approvedLocations.isEmpty) {
      return settings.enabled || settings.lastSyncedAt.isEmpty;
    }

    return settings.approvedLocations.any(
      (RecoveryApprovedLocation selected) =>
          selected.fullPath == location.fullPath,
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

  List<String> get _selectedDownloadablePaths {
    return _checkedPaths.toList(growable: false);
  }

  Future<void> _queueSelectedDownloads() async {
    final String? deviceId = _draftDeviceId ?? _selectedDeviceId;
    final List<String> selectedPaths = _selectedDownloadablePaths;

    if (deviceId == null || selectedPaths.isEmpty) {
      return;
    }

    setState(() {
      _isQueuingDownloads = true;
    });

    try {
      for (final String path in selectedPaths) {
        await ApiDeviceService().requestRecoveryDownload(
          deviceId: deviceId,
          filePath: path,
        );
      }

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            selectedPaths.length == 1
                ? 'Download request queued. Open File Transfer to watch it move to Ready.'
                : '${selectedPaths.length} download requests queued. Open File Transfer to watch them move to Ready.',
          ),
        ),
      );

      setState(() {
        _checkedPaths.clear();
      });
    } catch (error) {
      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error.toString())),
      );
    } finally {
      if (mounted) {
        setState(() {
          _isQueuingDownloads = false;
        });
      }
    }
  }

  Future<void> _saveRecoverySelection() async {
    if (_draftDeviceId == null || _draftLocations.isEmpty) {
      return;
    }

    final List<RecoveryApprovedLocation> selectedLocations = _draftLocations
        .where(
          (RecoveryApprovedLocation location) =>
              _draftSelections[location.fullPath] ?? false,
        )
        .toList();

    setState(() {
      _isSaving = true;
    });

    try {
      await ApiDeviceService().saveRecoverySettings(
        deviceId: _draftDeviceId!,
        deviceName: _draftDeviceName,
        enabled: selectedLocations.isNotEmpty,
        approvedLocations: selectedLocations,
      );

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Emergency Recovery selection saved. The background agent will apply it automatically.'),
        ),
      );

      setState(() {
        _pageFuture = _loadPageData();
      });
    } catch (error) {
      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error.toString())),
      );
    } finally {
      if (mounted) {
        setState(() {
          _isSaving = false;
        });
      }
    }
  }

  Future<void> _requestRecoveryScan() async {
    final String? deviceId = _draftDeviceId ?? _selectedDeviceId;
    if (deviceId == null) {
      return;
    }

    setState(() {
      _isRequestingScan = true;
    });

    try {
      await ApiDeviceService().requestRecoveryScan(deviceId);

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Recovery scan requested. The background agent will pick it up automatically and refresh this page after the next sync.',
          ),
        ),
      );
      _refresh();
    } catch (error) {
      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error.toString())),
      );
    } finally {
      if (mounted) {
        setState(() {
          _isRequestingScan = false;
        });
      }
    }
  }
  void _openRoot(String rootKey) {
    setState(() {
      _currentRootKey = rootKey;
      _currentFolderKey = rootKey;
      _checkedPaths.clear();
    });
  }

  void _openFolder(String folderKey) {
    setState(() {
      _currentFolderKey = folderKey;
      _checkedPaths.clear();
    });
  }

  void _goUp(_RecoveryExplorerModel explorer) {
    final String? currentFolderKey = _currentFolderKey;
    if (currentFolderKey == null) {
      return;
    }

    final _ExplorerNode? currentNode = explorer.nodesByPath[currentFolderKey];
    if (currentNode == null || currentNode.parentPath == null) {
      return;
    }

    setState(() {
      _currentFolderKey = currentNode.parentPath;
    });
  }

  void _toggleChecked(String path, bool? selected) {
    setState(() {
      if (selected ?? false) {
        _checkedPaths.add(path);
      } else {
        _checkedPaths.remove(path);
      }
    });
  }

  List<_ExplorerNode> _visibleChildren(_RecoveryExplorerModel explorer) {
    final String? currentFolderKey = _currentFolderKey;
    if (currentFolderKey == null) {
      return const <_ExplorerNode>[];
    }

    final List<_ExplorerNode> children =
        explorer.childrenByParent[currentFolderKey] ?? const <_ExplorerNode>[];
    final String query = _searchController.text.trim().toLowerCase();

    if (query.isEmpty) {
      return children;
    }

    return children.where((_ExplorerNode node) {
      return node.name.toLowerCase().contains(query) ||
          node.typeLabel.toLowerCase().contains(query);
    }).toList();
  }

  String _formatBytes(int bytes) {
    const List<String> units = <String>['B', 'KB', 'MB', 'GB', 'TB'];
    double size = bytes.toDouble();
    int unitIndex = 0;

    while (size >= 1024 && unitIndex < units.length - 1) {
      size /= 1024;
      unitIndex++;
    }

    return '${size.toStringAsFixed(size >= 100 || unitIndex == 0 ? 0 : 1)} ${units[unitIndex]}';
  }

  String _formatModifiedDate(String value) {
    if (value.isEmpty) {
      return 'Unknown';
    }

    final DateTime? parsed = DateTime.tryParse(value);
    if (parsed == null) {
      return value;
    }

    final DateTime local = parsed.toLocal();
    final String month = local.month.toString().padLeft(2, '0');
    final String day = local.day.toString().padLeft(2, '0');
    final String hour = (local.hour % 12 == 0 ? 12 : local.hour % 12)
        .toString()
        .padLeft(2, '0');
    final String minute = local.minute.toString().padLeft(2, '0');
    final String meridiem = local.hour >= 12 ? 'PM' : 'AM';

    return '${local.year}-$month-$day $hour:$minute $meridiem';
  }

  String _describeLocationPath(RecoveryApprovedLocation location) {
    switch (location.fullPath) {
      case '%FMD_DESKTOP%':
        return 'Current Windows user Desktop folder';
      case '%FMD_DOCUMENTS%':
        return 'Current Windows user Documents folder';
      case '%FMD_DOWNLOADS%':
        return 'Current Windows user Downloads folder';
      case '%FMD_PICTURES%':
        return 'Current Windows user Pictures folder';
      case '%FMD_VIDEOS%':
        return 'Current Windows user Videos folder';
      case '%FMD_MUSIC%':
        return 'Current Windows user Music folder';
      default:
        return location.fullPath;
    }
  }

  IconData _iconForNode(_ExplorerNode node) {
    if (node.isDirectory) {
      return Icons.folder_rounded;
    }

    final String extension = node.extension.toLowerCase();
    if (const <String>{'.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp'}
        .contains(extension)) {
      return Icons.image_rounded;
    }
    if (extension == '.pdf') {
      return Icons.picture_as_pdf_rounded;
    }
    if (const <String>{
      '.doc',
      '.docx',
      '.txt',
      '.rtf',
      '.xls',
      '.xlsx',
      '.ppt',
      '.pptx',
      '.csv',
    }.contains(extension)) {
      return Icons.description_rounded;
    }
    if (const <String>{'.mp4', '.mov', '.avi', '.mkv', '.wmv'}
        .contains(extension)) {
      return Icons.videocam_rounded;
    }
    if (const <String>{'.mp3', '.wav', '.aac', '.flac', '.m4a'}
        .contains(extension)) {
      return Icons.music_note_rounded;
    }
    if (const <String>{'.zip', '.rar', '.7z', '.tar', '.gz'}
        .contains(extension)) {
      return Icons.archive_rounded;
    }

    return Icons.insert_drive_file_rounded;
  }

  Widget _buildStatusCard(
    BuildContext context,
    RecoverySettings? settings,
    int totalFiles,
  ) {
    final bool enabled = settings?.enabled == true;
    final int folderCount = settings?.approvedLocations.length ?? 0;

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
                Expanded(
                  child: Text(
                    enabled
                        ? 'Emergency Recovery Enabled'
                        : 'Emergency Recovery Not Enabled',
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w800,
                        ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            const Text(
              'Browse synced recovery metadata by the real root folder. Direct file transfer stays hidden here until the completed transfer is actually ready.',
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: <Widget>[
                _buildInfoPill('Total Files', totalFiles.toString()),
                _buildInfoPill('Approved Roots', folderCount.toString()),
                _buildInfoPill(
                  'Last Scan Time',
                  (settings?.lastSyncedAt ?? '').isEmpty
                      ? 'Not scanned yet'
                      : _formatModifiedDate(settings!.lastSyncedAt),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildSelectableLocationsCard(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: LayoutBuilder(
          builder: (BuildContext context, BoxConstraints constraints) {
            final bool stackedHeader = constraints.maxWidth < 760;

            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                if (stackedHeader) ...<Widget>[
                  Text(
                    'Approved Recovery Locations',
                    style: Theme.of(context).textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w800,
                        ),
                  ),
                  const SizedBox(height: 12),
                  SizedBox(
                    width: double.infinity,
                    child: ActionButton(
                      label: _isSaving ? 'Saving...' : 'Save Selection',
                      icon: Icons.save_rounded,
                      onPressed: _isSaving ? () {} : _saveRecoverySelection,
                    ),
                  ),
                  const SizedBox(height: 12),
                  SizedBox(
                    width: double.infinity,
                    child: ActionButton(
                      label: _isRequestingScan
                          ? 'Requesting Scan...'
                          : 'Start Recovery Scan',
                      icon: Icons.play_circle_outline_rounded,
                      onPressed:
                          _isRequestingScan ? () {} : _requestRecoveryScan,
                      isPrimary: false,
                    ),
                  ),
                ] else
                  Row(
                    children: <Widget>[
                      Expanded(
                        child: Text(
                          'Approved Recovery Locations',
                          style:
                              Theme.of(context).textTheme.titleLarge?.copyWith(
                                    fontWeight: FontWeight.w800,
                                  ),
                        ),
                      ),
                      const SizedBox(width: 12),
                      ActionButton(
                        label: _isSaving ? 'Saving...' : 'Save Selection',
                        icon: Icons.save_rounded,
                        onPressed: _isSaving ? () {} : _saveRecoverySelection,
                      ),
                      const SizedBox(width: 12),
                      ActionButton(
                        label: _isRequestingScan
                            ? 'Requesting Scan...'
                            : 'Start Recovery Scan',
                        icon: Icons.play_circle_outline_rounded,
                        onPressed:
                            _isRequestingScan ? () {} : _requestRecoveryScan,
                        isPrimary: false,
                      ),
                    ],
                  ),
                const SizedBox(height: 10),
                const Text(
                  'Choose which folders and extra drives the agent is allowed to index for Emergency Recovery.',
                ),
                const SizedBox(height: 16),
                Wrap(
                  spacing: 14,
                  runSpacing: 14,
                  children: _draftLocations.map((RecoveryApprovedLocation location) {
                    return SizedBox(
                      width: 280,
                      child: CheckboxListTile(
                        value: _draftSelections[location.fullPath] ?? false,
                        controlAffinity: ListTileControlAffinity.leading,
                        contentPadding:
                            const EdgeInsets.symmetric(horizontal: 8),
                        title: Text(
                          location.label,
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(fontWeight: FontWeight.w700),
                        ),
                        subtitle: Text(
                          _describeLocationPath(location),
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),
                        onChanged: (bool? value) {
                          setState(() {
                            _draftSelections[location.fullPath] = value ?? false;
                          });
                        },
                      ),
                    );
                  }).toList(),
                ),
              ],
            );
          },
        ),
      ),
    );
  }

  Widget _buildExplorerCard(
    BuildContext context,
    _RecoveryExplorerModel explorer,
    RecoverySettings? settings,
  ) {
    final List<_ExplorerNode> visibleChildren = _visibleChildren(explorer);
    final bool canGoUp = _currentFolderKey != null &&
        explorer.nodesByPath[_currentFolderKey!]?.parentPath != null;
    final _ExplorerNode? currentFolder =
        _currentFolderKey == null ? null : explorer.nodesByPath[_currentFolderKey!];
    final List<_RootSummary> roots = explorer.buildRootSummaries(
      settings?.approvedLocations ?? const <RecoveryApprovedLocation>[],
      settings?.lastSyncedAt ?? '',
    );

    return Card(
      clipBehavior: Clip.antiAlias,
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            LayoutBuilder(
              builder: (BuildContext context, BoxConstraints constraints) {
                final bool stackedHeader = constraints.maxWidth < 760;

                final Widget title = Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    Text(
                      'Recovery File Browser',
                      style: Theme.of(context).textTheme.titleLarge?.copyWith(
                            fontWeight: FontWeight.w800,
                          ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      explorer.fileEntries.isEmpty
                          ? 'No recovery files found. Run the agent and sync Emergency Recovery first.'
                          : 'Browse files by the real synced root folder.',
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                            color: Colors.black54,
                          ),
                    ),
                  ],
                );

                if (stackedHeader) {
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      title,
                      const SizedBox(height: 12),
                      SizedBox(
                        width: double.infinity,
                        child: ActionButton(
                          label: 'Refresh File List',
                          icon: Icons.refresh_rounded,
                          onPressed: _refresh,
                          isPrimary: false,
                        ),
                      ),
                    ],
                  );
                }

                return Row(
                  children: <Widget>[
                    Expanded(child: title),
                    const SizedBox(width: 12),
                    ActionButton(
                      label: 'Refresh File List',
                      icon: Icons.refresh_rounded,
                      onPressed: _refresh,
                      isPrimary: false,
                    ),
                  ],
                );
              },
            ),
            const SizedBox(height: 16),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: <Widget>[
                SizedBox(
                  width: 280,
                  child: TextField(
                    controller: _searchController,
                    decoration: const InputDecoration(
                      prefixIcon: Icon(Icons.search_rounded),
                      labelText: 'Search this folder',
                      border: OutlineInputBorder(),
                    ),
                  ),
                ),
                OutlinedButton.icon(
                  onPressed: canGoUp ? () => _goUp(explorer) : null,
                  icon: const Icon(Icons.arrow_upward_rounded),
                  label: const Text('Up One Folder'),
                ),
                Text(
                  '${_checkedPaths.length} selected',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                ),
                if (_selectedDownloadablePaths.isNotEmpty)
                  ActionButton(
                    label: _isQueuingDownloads
                        ? 'Queueing...'
                        : 'Download Selected',
                    icon: Icons.download_rounded,
                    onPressed:
                        _isQueuingDownloads ? () {} : _queueSelectedDownloads,
                    isPrimary: false,
                  ),
              ],
            ),
            if (_checkedPaths.isNotEmpty) ...<Widget>[
              const SizedBox(height: 10),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: const Color(0xFFF8FAFC),
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: const Color(0xFFD9E2EC)),
                ),
                child: const Text(
                  'Selected files can be queued now. Use File Transfer to watch the job move from Pending to Ready, then download it there.',
                ),
              ),
            ],
            const SizedBox(height: 16),
            LayoutBuilder(
              builder: (BuildContext context, BoxConstraints constraints) {
                final bool stacked = constraints.maxWidth < 980;
                final Widget rootsPane = _buildRootPane(context, roots);
                final Widget browserPane = _buildBrowserPane(
                  context,
                  explorer,
                  currentFolder,
                  visibleChildren,
                );

                if (stacked) {
                  return Column(
                    children: <Widget>[
                      rootsPane,
                      const SizedBox(height: 16),
                      browserPane,
                    ],
                  );
                }

                return Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    SizedBox(width: 260, child: rootsPane),
                    const SizedBox(width: 18),
                    Expanded(child: browserPane),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildRootPane(BuildContext context, List<_RootSummary> roots) {
    return Container(
      decoration: BoxDecoration(
        color: const Color(0xFFF8FAFC),
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: Colors.black.withValues(alpha: 0.06)),
      ),
      padding: const EdgeInsets.all(14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            'Recovery Roots',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w800,
                ),
          ),
          const SizedBox(height: 10),
          if (roots.isEmpty)
            const Text('No synced folders yet.')
          else
            ...roots.map((_RootSummary root) {
              final bool selected = root.key == _currentRootKey;
              final String detail = root.fileCount > 0
                  ? '${root.fileCount} file${root.fileCount == 1 ? '' : 's'}'
                  : root.detail;
              return Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Material(
                  color: selected
                      ? Theme.of(context)
                          .colorScheme
                          .primary
                          .withValues(alpha: 0.10)
                      : Colors.white,
                  borderRadius: BorderRadius.circular(14),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(14),
                    onTap: () => _openRoot(root.key),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 12,
                        vertical: 12,
                      ),
                      child: Row(
                        children: <Widget>[
                          Icon(
                            root.label.endsWith(':')
                                ? Icons.storage_rounded
                                : Icons.folder_special_rounded,
                            color: selected
                                ? Theme.of(context).colorScheme.primary
                                : const Color(0xFF486581),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: <Widget>[
                                Text(
                                  root.label,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w700,
                                  ),
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  detail,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: Theme.of(context)
                                      .textTheme
                                      .bodySmall
                                      ?.copyWith(color: Colors.black54),
                                ),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              );
            }),
        ],
      ),
    );
  }

  Widget _buildBrowserPane(
    BuildContext context,
    _RecoveryExplorerModel explorer,
    _ExplorerNode? currentFolder,
    List<_ExplorerNode> visibleChildren,
  ) {
    return Container(
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: Colors.black.withValues(alpha: 0.06)),
      ),
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          _buildBreadcrumbs(context, explorer, currentFolder),
          const SizedBox(height: 12),
          _buildColumnHeader(context),
          const Divider(height: 1),
          SizedBox(
            height: 520,
            child: visibleChildren.isEmpty
                ? Center(
                    child: Text(
                      currentFolder == null
                          ? 'No recovery files found. Run the agent and sync Emergency Recovery first.'
                          : 'No files found.',
                    ),
                  )
                : ListView.separated(
                    itemCount: visibleChildren.length,
                    separatorBuilder: (_, __) => const Divider(height: 1),
                    itemBuilder: (BuildContext context, int index) {
                      final _ExplorerNode node = visibleChildren[index];
                      return _ExplorerRow(
                        node: node,
                        checked: _checkedPaths.contains(node.fullPath),
                        icon: _iconForNode(node),
                        formattedSize:
                            node.isDirectory ? '' : _formatBytes(node.sizeBytes),
                        formattedModifiedDate:
                            _formatModifiedDate(node.lastModified),
                        onChanged: node.isDirectory
                            ? null
                            : (bool? value) =>
                                _toggleChecked(node.fullPath, value),
                        onOpen:
                            node.isDirectory ? () => _openFolder(node.fullPath) : null,
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }

  Widget _buildBreadcrumbs(
    BuildContext context,
    _RecoveryExplorerModel explorer,
    _ExplorerNode? currentFolder,
  ) {
    final List<_ExplorerNode> trail =
        explorer.breadcrumbsFor(currentFolder?.fullPath);

    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(
        children: <Widget>[
          Text(
            'Recovery',
            style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
          ),
          for (final _ExplorerNode node in trail) ...<Widget>[
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: 8),
              child: Icon(Icons.chevron_right_rounded, size: 18),
            ),
            InkWell(
              onTap: () => _openFolder(node.fullPath),
              borderRadius: BorderRadius.circular(8),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
                child: Text(
                  node.name,
                  style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                        color: Theme.of(context).colorScheme.primary,
                        fontWeight: FontWeight.w700,
                      ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildColumnHeader(BuildContext context) {
    final TextStyle headerStyle =
        Theme.of(context).textTheme.bodySmall!.copyWith(
              fontWeight: FontWeight.w800,
              color: const Color(0xFF52606D),
            );

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
      child: Row(
        children: <Widget>[
          const SizedBox(width: 40),
          Expanded(flex: 5, child: Text('Name', style: headerStyle)),
          Expanded(flex: 2, child: Text('Type', style: headerStyle)),
          Expanded(flex: 2, child: Text('Size', style: headerStyle)),
          Expanded(flex: 3, child: Text('Modified Date', style: headerStyle)),
        ],
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

          final _EmergencyRecoveryPageData pageData = snapshot.data ??
              const _EmergencyRecoveryPageData(
                devices: <dynamic>[],
                selectedDeviceId: null,
                settings: null,
                files: <RecoveryFileEntry>[],
                selectableLocations: <RecoveryApprovedLocation>[],
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

          final _RecoveryExplorerModel explorer = _RecoveryExplorerModel.fromEntries(
            pageData.files,
            pageData.settings?.approvedLocations ?? const <RecoveryApprovedLocation>[],
          );

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
                                child: Text(
                                  '${device['deviceName'] ?? 'Unknown Device'}',
                                ),
                              ),
                            )
                            .toList(),
                        onChanged: (String? value) {
                          if (value == null || value == _selectedDeviceId) {
                            return;
                          }

                          setState(() {
                            _selectedDeviceId = value;
                            _draftDeviceId = null;
                            _pageFuture = _loadPageData();
                          });
                        },
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 16),
              _buildStatusCard(context, pageData.settings, pageData.files.length),
              const SizedBox(height: 16),
              _buildSelectableLocationsCard(context),
              const SizedBox(height: 16),
              _buildExplorerCard(context, explorer, pageData.settings),
            ],
          );
        },
      ),
    );
  }

  Widget _buildInfoPill(String label, String value) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: const Color(0xFFF4F7FB),
        borderRadius: BorderRadius.circular(999),
        border: Border.all(color: const Color(0xFFD9E2EC)),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        child: RichText(
          text: TextSpan(
            style: const TextStyle(
              color: Color(0xFF243B53),
              fontSize: 13,
            ),
            children: <TextSpan>[
              TextSpan(
                text: '$label: ',
                style: const TextStyle(fontWeight: FontWeight.w700),
              ),
              TextSpan(text: value),
            ],
          ),
        ),
      ),
    );
  }
}

class _ExplorerRow extends StatelessWidget {
  const _ExplorerRow({
    required this.node,
    required this.checked,
    required this.icon,
    required this.formattedSize,
    required this.formattedModifiedDate,
    required this.onChanged,
    required this.onOpen,
  });

  final _ExplorerNode node;
  final bool checked;
  final IconData icon;
  final String formattedSize;
  final String formattedModifiedDate;
  final ValueChanged<bool?>? onChanged;
  final VoidCallback? onOpen;

  @override
  Widget build(BuildContext context) {
    final Widget nameCell = Row(
      children: <Widget>[
        Icon(icon, color: const Color(0xFF486581)),
        const SizedBox(width: 10),
        Expanded(
          child: Tooltip(
            message: node.fullPath,
            child: Text(
              node.name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontWeight:
                    node.isDirectory ? FontWeight.w700 : FontWeight.w500,
                color:
                    onOpen != null ? Theme.of(context).colorScheme.primary : null,
              ),
            ),
          ),
        ),
      ],
    );

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      child: Row(
        children: <Widget>[
          SizedBox(
            width: 40,
            child: Checkbox(
              value: checked,
              onChanged: onChanged,
            ),
          ),
          Expanded(
            flex: 5,
            child: onOpen == null
                ? nameCell
                : InkWell(
                    onTap: onOpen,
                    borderRadius: BorderRadius.circular(8),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        vertical: 6,
                        horizontal: 4,
                      ),
                      child: nameCell,
                    ),
                  ),
          ),
          Expanded(
            flex: 2,
            child: Text(
              node.typeLabel,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          Expanded(
            flex: 2,
            child: Text(
              formattedSize,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          Expanded(
            flex: 3,
            child: Text(
              formattedModifiedDate,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ],
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
    required this.selectableLocations,
  });

  final List<dynamic> devices;
  final String? selectedDeviceId;
  final RecoverySettings? settings;
  final List<RecoveryFileEntry> files;
  final List<RecoveryApprovedLocation> selectableLocations;
}

class _RecoveryExplorerModel {
  const _RecoveryExplorerModel({
    required this.roots,
    required this.nodesByPath,
    required this.childrenByParent,
    required this.fileEntries,
  });

  final List<_ExplorerNode> roots;
  final Map<String, _ExplorerNode> nodesByPath;
  final Map<String, List<_ExplorerNode>> childrenByParent;
  final List<RecoveryFileEntry> fileEntries;

  factory _RecoveryExplorerModel.fromEntries(
    List<RecoveryFileEntry> entries,
    List<RecoveryApprovedLocation> approvedLocations,
  ) {
    final Map<String, _ExplorerNode> nodesByPath = <String, _ExplorerNode>{};
    final Map<String, _ExplorerNode> rootNodes = <String, _ExplorerNode>{};

    for (final RecoveryFileEntry entry in entries) {
      final String fullPath = _normalizePath(entry.fullPath);
      if (fullPath.isEmpty) {
        continue;
      }

      final String rootPath = _normalizePath(entry.rootPath);
      final String rootKey = rootPath.isEmpty ? fullPath : rootPath;
      final String rootLabel = entry.rootLabel.trim().isEmpty
          ? _labelForRoot(rootKey)
          : entry.rootLabel.trim();

      rootNodes.putIfAbsent(
        rootKey,
        () => _ExplorerNode(
          fullPath: rootKey,
          name: rootLabel,
          rootPath: rootKey,
          parentPath: null,
          isDirectory: true,
          extension: '',
          sizeBytes: 0,
          lastModified: '',
          typeLabel: 'Folder',
        ),
      );

      nodesByPath[fullPath] = _ExplorerNode(
        fullPath: fullPath,
        name: _leafName(fullPath, rootKey, rootLabel),
        rootPath: rootKey,
        parentPath: null,
        isDirectory: entry.isDirectory,
        extension: entry.extension,
        sizeBytes: entry.sizeBytes,
        lastModified: entry.lastModified,
        typeLabel: _typeLabel(entry.isDirectory, entry.extension),
      );
    }

    for (final RecoveryApprovedLocation location in approvedLocations) {
      final _ExplorerNode? existingRoot = rootNodes.values.cast<_ExplorerNode?>()
          .firstWhere(
            (_ExplorerNode? node) =>
                node != null && _matchesApprovedLocation(node, location),
            orElse: () => null,
          );
      if (existingRoot != null) {
        continue;
      }

      final String virtualRootKey = _virtualRootKey(location);
      rootNodes[virtualRootKey] = _ExplorerNode(
        fullPath: virtualRootKey,
        name: location.label,
        rootPath: virtualRootKey,
        parentPath: null,
        isDirectory: true,
        extension: '',
        sizeBytes: 0,
        lastModified: '',
        typeLabel: 'Folder',
      );
    }

    for (final _ExplorerNode root in rootNodes.values) {
      nodesByPath.putIfAbsent(root.fullPath, () => root);
    }

    for (final _ExplorerNode node in nodesByPath.values.toList()) {
      if (node.fullPath == node.rootPath) {
        continue;
      }

      _ensureAncestors(nodesByPath, rootNodes[node.rootPath]!, node);
    }

    final Map<String, _ExplorerNode> finalizedNodes = <String, _ExplorerNode>{};
    for (final _ExplorerNode node in nodesByPath.values) {
      finalizedNodes[node.fullPath] = node.copyWith(
        parentPath: _parentPathFor(node.fullPath, node.rootPath),
      );
    }

    final Map<String, List<_ExplorerNode>> childrenByParent =
        <String, List<_ExplorerNode>>{};
    for (final _ExplorerNode node in finalizedNodes.values) {
      if (node.fullPath == node.rootPath) {
        continue;
      }

      final String? parentPath = node.parentPath;
      if (parentPath == null) {
        continue;
      }

      childrenByParent.putIfAbsent(parentPath, () => <_ExplorerNode>[]).add(node);
    }

    for (final List<_ExplorerNode> nodes in childrenByParent.values) {
      nodes.sort(_compareNodes);
    }

    final List<_ExplorerNode> roots = rootNodes.keys
        .map((String rootKey) => finalizedNodes[rootNodes[rootKey]!.fullPath]!)
        .toList()
      ..sort(_compareNodes);

    return _RecoveryExplorerModel(
      roots: roots,
      nodesByPath: finalizedNodes,
      childrenByParent: childrenByParent,
      fileEntries: entries,
    );
  }

  List<_ExplorerNode> breadcrumbsFor(String? currentPath) {
    if (currentPath == null) {
      return const <_ExplorerNode>[];
    }

    final _ExplorerNode? currentNode = nodesByPath[currentPath];
    if (currentNode == null) {
      return const <_ExplorerNode>[];
    }

    final List<_ExplorerNode> trail = <_ExplorerNode>[];
    _ExplorerNode? cursor = currentNode;

    while (cursor != null) {
      trail.add(cursor);
      final String? parentPath = cursor.parentPath;
      cursor = parentPath == null ? null : nodesByPath[parentPath];
    }

    return trail.reversed.toList();
  }

  List<_RootSummary> buildRootSummaries(
    List<RecoveryApprovedLocation> approvedLocations,
    String lastSyncedAt,
  ) {
    final bool hasScan = lastSyncedAt.isNotEmpty;
    final Map<String, int> fileCountsByRoot = <String, int>{};

    for (final RecoveryFileEntry entry in fileEntries) {
      final String rootPath = _normalizePath(entry.rootPath);
      if (rootPath.isEmpty || entry.isDirectory) {
        continue;
      }

      fileCountsByRoot[rootPath] = (fileCountsByRoot[rootPath] ?? 0) + 1;
    }

    if (approvedLocations.isEmpty) {
      return roots.map((_ExplorerNode root) {
        final int fileCount = fileCountsByRoot[root.rootPath] ?? 0;
        return _RootSummary(
          key: root.fullPath,
          label: root.name,
          fileCount: fileCount,
          detail: fileCount > 0
              ? '$fileCount files'
              : (hasScan ? 'Synced, no files found' : 'Run the agent sync to load this folder'),
        );
      }).toList();
    }

    return approvedLocations.map((_buildRootSummaryForApprovedLocation(
          fileCountsByRoot,
          hasScan,
        ))).toList();
  }

  _RootSummary Function(RecoveryApprovedLocation) _buildRootSummaryForApprovedLocation(
    Map<String, int> fileCountsByRoot,
    bool hasScan,
  ) {
    return (RecoveryApprovedLocation location) {
      final _ExplorerNode? matchedRoot = roots.cast<_ExplorerNode?>().firstWhere(
            (_ExplorerNode? node) =>
                node != null && _matchesApprovedLocation(node, location),
            orElse: () => null,
          );
      final String key = matchedRoot?.fullPath ?? _virtualRootKey(location);
      final int fileCount = matchedRoot == null
          ? 0
          : (fileCountsByRoot[matchedRoot.rootPath] ?? 0);

      return _RootSummary(
        key: key,
        label: location.label,
        fileCount: fileCount,
        detail: fileCount > 0
            ? '$fileCount files'
            : (hasScan ? 'Synced, no files found' : 'Run the agent sync to load this folder'),
      );
    };
  }

  static bool _matchesApprovedLocation(
    _ExplorerNode node,
    RecoveryApprovedLocation location,
  ) {
    final String locationPath = _normalizePath(location.fullPath);
    if (locationPath.isNotEmpty &&
        locationPath == node.rootPath &&
        !locationPath.startsWith('%')) {
      return true;
    }

    return node.name.toLowerCase() == location.label.toLowerCase();
  }

  static void _ensureAncestors(
    Map<String, _ExplorerNode> nodesByPath,
    _ExplorerNode rootNode,
    _ExplorerNode node,
  ) {
    final List<String> ancestors = _ancestorPaths(node.fullPath, rootNode.rootPath);
    for (final String ancestorPath in ancestors) {
      nodesByPath.putIfAbsent(
        ancestorPath,
        () => _ExplorerNode(
          fullPath: ancestorPath,
          name: ancestorPath == rootNode.rootPath
              ? rootNode.name
              : _leafName(ancestorPath, rootNode.rootPath, rootNode.name),
          rootPath: rootNode.rootPath,
          parentPath: null,
          isDirectory: true,
          extension: '',
          sizeBytes: 0,
          lastModified: '',
          typeLabel: 'Folder',
        ),
      );
    }
  }

  static List<String> _ancestorPaths(String fullPath, String rootPath) {
    final List<String> ancestors = <String>[rootPath];
    if (fullPath == rootPath) {
      return ancestors;
    }

    String? cursor = _parentPathFor(fullPath, rootPath);
    while (cursor != null) {
      ancestors.add(cursor);
      cursor = cursor == rootPath ? null : _parentPathFor(cursor, rootPath);
    }

    return ancestors.reversed.toSet().toList();
  }

  static int _compareNodes(_ExplorerNode left, _ExplorerNode right) {
    if (left.isDirectory != right.isDirectory) {
      return left.isDirectory ? -1 : 1;
    }

    return left.name.toLowerCase().compareTo(right.name.toLowerCase());
  }

  static String? _parentPathFor(String fullPath, String rootPath) {
    if (fullPath == rootPath) {
      return null;
    }

    final String trimmed = fullPath.endsWith('\\') && fullPath.length > 3
        ? fullPath.substring(0, fullPath.length - 1)
        : fullPath;
    final int index = trimmed.lastIndexOf('\\');
    if (index <= 1 || rootPath.startsWith('virtual::')) {
      return rootPath;
    }

    final String parent = trimmed.substring(0, index);
    return parent.length < rootPath.length ? rootPath : parent;
  }

  static String _leafName(String fullPath, String rootPath, String rootLabel) {
    if (fullPath == rootPath) {
      return rootLabel;
    }

    final String trimmed = fullPath.endsWith('\\') && fullPath.length > 3
        ? fullPath.substring(0, fullPath.length - 1)
        : fullPath;
    final int index = trimmed.lastIndexOf('\\');
    if (index == -1) {
      return trimmed;
    }

    return trimmed.substring(index + 1);
  }

  static String _labelForRoot(String rootPath) {
    final String trimmed = rootPath.endsWith('\\') && rootPath.length > 3
        ? rootPath.substring(0, rootPath.length - 1)
        : rootPath;
    final List<String> segments =
        trimmed.split('\\').where((String segment) => segment.isNotEmpty).toList();

    if (segments.isEmpty) {
      return rootPath;
    }

    if (segments.length == 1 && segments.first.endsWith(':')) {
      return segments.first.toUpperCase();
    }

    return segments.last;
  }

  static String _virtualRootKey(RecoveryApprovedLocation location) {
    return 'virtual::${location.label}::${location.fullPath}';
  }

  static String _normalizePath(String path) {
    final String trimmed = path.trim().replaceAll('/', r'\');
    if (trimmed.isEmpty) {
      return '';
    }

    if (trimmed.startsWith('virtual::')) {
      return trimmed;
    }

    final StringBuffer buffer = StringBuffer();
    bool previousWasSlash = false;
    for (final int rune in trimmed.runes) {
      final String char = String.fromCharCode(rune);
      if (char == r'\') {
        if (!previousWasSlash) {
          buffer.write(char);
        }
        previousWasSlash = true;
      } else {
        previousWasSlash = false;
        buffer.write(char);
      }
    }

    String normalized = buffer.toString();
    if (RegExp(r'^[A-Za-z]:$').hasMatch(normalized)) {
      normalized = '${normalized.toUpperCase()}\\';
    }

    return normalized;
  }

  static String _typeLabel(bool isDirectory, String extension) {
    if (isDirectory) {
      return 'Folder';
    }

    final String ext = extension.toLowerCase();
    if (const <String>{'.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp'}
        .contains(ext)) {
      return 'Image';
    }
    if (ext == '.pdf') {
      return 'PDF';
    }
    if (const <String>{
      '.doc',
      '.docx',
      '.txt',
      '.rtf',
      '.xls',
      '.xlsx',
      '.ppt',
      '.pptx',
      '.csv',
    }.contains(ext)) {
      return 'Document';
    }
    if (const <String>{'.mp4', '.mov', '.avi', '.mkv', '.wmv'}.contains(ext)) {
      return 'Video';
    }
    if (const <String>{'.mp3', '.wav', '.aac', '.flac', '.m4a'}.contains(ext)) {
      return 'Music';
    }
    if (const <String>{'.zip', '.rar', '.7z', '.tar', '.gz'}.contains(ext)) {
      return 'Zip';
    }

    return 'File';
  }
}

class _RootSummary {
  const _RootSummary({
    required this.key,
    required this.label,
    required this.fileCount,
    required this.detail,
  });

  final String key;
  final String label;
  final int fileCount;
  final String detail;
}

class _ExplorerNode {
  const _ExplorerNode({
    required this.fullPath,
    required this.name,
    required this.rootPath,
    required this.parentPath,
    required this.isDirectory,
    required this.extension,
    required this.sizeBytes,
    required this.lastModified,
    required this.typeLabel,
  });

  final String fullPath;
  final String name;
  final String rootPath;
  final String? parentPath;
  final bool isDirectory;
  final String extension;
  final int sizeBytes;
  final String lastModified;
  final String typeLabel;

  _ExplorerNode copyWith({
    String? parentPath,
  }) {
    return _ExplorerNode(
      fullPath: fullPath,
      name: name,
      rootPath: rootPath,
      parentPath: parentPath ?? this.parentPath,
      isDirectory: isDirectory,
      extension: extension,
      sizeBytes: sizeBytes,
      lastModified: lastModified,
      typeLabel: typeLabel,
    );
  }
}





