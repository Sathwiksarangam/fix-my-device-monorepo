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
  List<RecoveryApprovedLocation> _draftLocations = <RecoveryApprovedLocation>[];
  Map<String, bool> _draftSelections = <String, bool>{};
  String? _currentRootPath;
  String? _currentFolderPath;
  final Set<String> _checkedPaths = <String>{};
  final Set<String> _directoryPaths = <String>{};

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
    if (!mounted) {
      return;
    }

    setState(() {});
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
        selectableLocations: <RecoveryApprovedLocation>[],
      );
    }

    final selectedDevice = devices.firstWhere(
      (dynamic device) => '${device['id'] ?? ''}' == (_selectedDeviceId ?? ''),
      orElse: () => devices.first,
    );

    final resolvedDeviceId = '${selectedDevice['id'] ?? ''}';
    final resolvedDeviceName = '${selectedDevice['deviceName'] ?? 'Unknown Device'}';
    _selectedDeviceId = resolvedDeviceId;

    final results = await Future.wait<dynamic>(<Future<dynamic>>[
      api.getRecoverySettings(resolvedDeviceId),
      api.getRecoveryFileList(resolvedDeviceId),
    ]);

    final RecoverySettings settings = results[0] as RecoverySettings;
    final List<RecoveryFileEntry> files = results[1] as List<RecoveryFileEntry>;
    _directoryPaths
      ..clear()
      ..addAll(files
          .where((RecoveryFileEntry entry) => entry.isDirectory)
          .map((RecoveryFileEntry entry) => entry.fullPath));
    final selectableLocations = _buildSelectableLocations(selectedDevice, settings);
    final explorer = _RecoveryExplorerModel.fromEntries(files);

    if (_draftDeviceId != resolvedDeviceId) {
      _draftDeviceId = resolvedDeviceId;
      _draftDeviceName = resolvedDeviceName;
      _draftLocations = selectableLocations;
      _draftSelections = <String, bool>{
        for (final RecoveryApprovedLocation location in selectableLocations)
          location.fullPath: _isLocationSelected(location, settings),
      };
      _checkedPaths.clear();
      _currentRootPath = explorer.roots.isNotEmpty ? explorer.roots.first.fullPath : null;
      _currentFolderPath = _currentRootPath;
      _searchController.clear();
    } else {
      if (_currentRootPath == null ||
          !explorer.roots.any((_ExplorerNode node) => node.fullPath == _currentRootPath)) {
        _currentRootPath = explorer.roots.isNotEmpty ? explorer.roots.first.fullPath : null;
      }

      if (_currentFolderPath == null ||
          !explorer.nodesByPath.containsKey(_currentFolderPath)) {
        _currentFolderPath = _currentRootPath;
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

      final String normalizedDriveLetter = driveLetter.endsWith(r'\')
          ? driveLetter.substring(0, driveLetter.length - 1)
          : driveLetter;

      addLocation(RecoveryApprovedLocation(
        label: normalizedDriveLetter,
        fullPath: '$normalizedDriveLetter\\',
        driveLetter: normalizedDriveLetter,
        locationType: 'Drive',
      ));
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
      (RecoveryApprovedLocation selected) => selected.fullPath == location.fullPath,
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

  List<String> get _selectedDownloadablePaths => _checkedPaths
      .where((String path) => !_directoryPaths.contains(path))
      .toList(growable: false);

  Future<void> _requestSelectedDownloads() async {
    final String? deviceId = _draftDeviceId ?? _selectedDeviceId;
    if (deviceId == null) {
      return;
    }

    final List<String> selectedPaths = _selectedDownloadablePaths;
    if (selectedPaths.isEmpty) {
      return;
    }

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
                ? 'Download request created. Check File Transfer for status.'
                : '${selectedPaths.length} download requests created. Check File Transfer for status.',
          ),
        ),
      );
    } catch (error) {
      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error.toString())),
      );
    }
  }

  Future<void> _saveRecoverySelection() async {
    if (_draftDeviceId == null || _draftLocations.isEmpty) {
      return;
    }

    final List<RecoveryApprovedLocation> selectedLocations = _draftLocations
        .where((RecoveryApprovedLocation location) => _draftSelections[location.fullPath] ?? false)
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
          content: Text('Emergency Recovery selection saved. Run the agent sync next.'),
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

  void _openRoot(String rootPath) {
    setState(() {
      _currentRootPath = rootPath;
      _currentFolderPath = rootPath;
      _checkedPaths.clear();
    });
  }

  void _openFolder(String folderPath) {
    setState(() {
      _currentFolderPath = folderPath;
      _checkedPaths.clear();
    });
  }

  void _goUp(_RecoveryExplorerModel explorer) {
    final String? currentFolderPath = _currentFolderPath;
    if (currentFolderPath == null) {
      return;
    }

    final _ExplorerNode? currentNode = explorer.nodesByPath[currentFolderPath];
    if (currentNode == null || currentNode.parentPath == null) {
      return;
    }

    setState(() {
      _currentFolderPath = currentNode.parentPath;
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
    final String? currentFolderPath = _currentFolderPath;
    if (currentFolderPath == null) {
      return const <_ExplorerNode>[];
    }

    final List<_ExplorerNode> children =
        explorer.childrenByParent[currentFolderPath] ?? const <_ExplorerNode>[];
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
    var unitIndex = 0;

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
    if (const <String>{'.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp'}.contains(extension)) {
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
    if (const <String>{'.mp4', '.mov', '.avi', '.mkv', '.wmv'}.contains(extension)) {
      return Icons.videocam_rounded;
    }
    if (const <String>{'.mp3', '.wav', '.aac', '.flac', '.m4a'}.contains(extension)) {
      return Icons.music_note_rounded;
    }
    if (const <String>{'.zip', '.rar', '.7z', '.tar', '.gz'}.contains(extension)) {
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
                  enabled ? Icons.verified_user_rounded : Icons.warning_amber_rounded,
                  color: enabled
                      ? Theme.of(context).colorScheme.primary
                      : const Color(0xFF9A6700),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    enabled ? 'Emergency Recovery Enabled' : 'Emergency Recovery Not Enabled',
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
              'File transfer is coming next. This version prepares the recovery file list only.',
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              children: <Widget>[
                _buildInfoPill('Total Files', totalFiles.toString()),
                _buildInfoPill('Approved Folders', folderCount.toString()),
                _buildInfoPill(
                  'Last Scan Time',
                  (settings?.lastSyncedAt ?? '').isEmpty
                      ? 'Not scanned yet'
                      : _formatModifiedDate(settings!.lastSyncedAt),
                ),
              ],
            ),
            if ((settings?.lastSyncedAt ?? '').isNotEmpty) ...<Widget>[
              const SizedBox(height: 10),
              Text(
                'Last scan time: ${_formatModifiedDate(settings!.lastSyncedAt)}',
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
                ] else
                  Row(
                    children: <Widget>[
                      Expanded(
                        child: Text(
                          'Approved Recovery Locations',
                          style: Theme.of(context).textTheme.titleLarge?.copyWith(
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
                    ],
                  ),
                const SizedBox(height: 10),
                const Text(
                  'Choose which folders and non-system drives the agent is allowed to index for recovery metadata.',
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
                        contentPadding: const EdgeInsets.symmetric(horizontal: 8),
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
    List<RecoveryFileEntry> files,
  ) {
    final List<_ExplorerNode> visibleChildren = _visibleChildren(explorer);
    final bool canGoUp = _currentFolderPath != null &&
        explorer.nodesByPath[_currentFolderPath!]?.parentPath != null;
    final _ExplorerNode? currentFolder =
        _currentFolderPath == null ? null : explorer.nodesByPath[_currentFolderPath!];

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

                if (stackedHeader) {
                  return Column(
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
                        files.isEmpty
                            ? 'Run the agent and sync Emergency Recovery first.'
                            : 'Browse synced recovery metadata like a simple file explorer.',
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                              color: Colors.black54,
                            ),
                      ),
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
                    Expanded(
                      child: Column(
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
                            files.isEmpty
                                ? 'Run the agent and sync Emergency Recovery first.'
                                : 'Browse synced recovery metadata like a simple file explorer.',
                            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                                  color: Colors.black54,
                                ),
                          ),
                        ],
                      ),
                    ),
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
                  OutlinedButton.icon(
                    onPressed: _requestSelectedDownloads,
                    icon: const Icon(Icons.download_rounded),
                    label: Text(
                      _selectedDownloadablePaths.length == 1
                          ? 'Download'
                          : 'Download Selected',
                    ),
                  ),
              ],
            ),
            const SizedBox(height: 16),
            LayoutBuilder(
              builder: (BuildContext context, BoxConstraints constraints) {
                final bool stacked = constraints.maxWidth < 980;
                final Widget rootsPane = _buildRootPane(context, explorer);
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
                    SizedBox(width: 250, child: rootsPane),
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

  Widget _buildRootPane(BuildContext context, _RecoveryExplorerModel explorer) {
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
          if (explorer.roots.isEmpty)
            const Text('No synced folders yet.')
          else
            ...explorer.roots.map((_ExplorerNode rootNode) {
              final bool selected = rootNode.fullPath == _currentRootPath;
              return Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Material(
                  color: selected
                      ? Theme.of(context).colorScheme.primary.withValues(alpha: 0.10)
                      : Colors.white,
                  borderRadius: BorderRadius.circular(14),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(14),
                    onTap: () => _openRoot(rootNode.fullPath),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
                      child: Row(
                        children: <Widget>[
                          Icon(
                            rootNode.fullPath.toUpperCase().startsWith('C:\\')
                                ? Icons.folder_special_rounded
                                : Icons.storage_rounded,
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
                                  rootNode.name,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(fontWeight: FontWeight.w700),
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  rootNode.fullPath,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                        color: Colors.black54,
                                      ),
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
          if (_checkedPaths.length == 1)
            Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: Text(
                'Selected path: ${_checkedPaths.first}',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: Colors.black54,
                    ),
              ),
            ),
          _buildColumnHeader(context),
          const Divider(height: 1),
          SizedBox(
            height: 520,
            child: visibleChildren.isEmpty
                ? Center(
                    child: Text(
                      explorer.roots.isEmpty
                          ? 'No recovery files found. Run the agent and sync Emergency Recovery first.'
                          : 'No files or folders match this view.',
                    ),
                  )
                : ListView.separated(
                    itemCount: visibleChildren.length,
                    separatorBuilder: (_, __) => const Divider(height: 1),
                    itemBuilder: (BuildContext context, int index) {
                      final _ExplorerNode node = visibleChildren[index];
                      final bool checked = _checkedPaths.contains(node.fullPath);

                      return _ExplorerRow(
                        node: node,
                        checked: checked,
                        icon: _iconForNode(node),
                        formattedSize: node.isDirectory ? '' : _formatBytes(node.sizeBytes),
                        formattedModifiedDate: _formatModifiedDate(node.lastModified),
                        onChanged: (bool? value) => _toggleChecked(node.fullPath, value),
                        onOpen: node.isDirectory ? () => _openFolder(node.fullPath) : null,
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
    final List<_ExplorerNode> trail = explorer.breadcrumbsFor(currentFolder?.fullPath);

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
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
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
    final TextStyle headerStyle = Theme.of(context).textTheme.bodySmall!.copyWith(
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
      subtitle: 'Prepare safe, metadata-only file recovery before a laptop screen fails.',
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

          final _RecoveryExplorerModel explorer =
              _RecoveryExplorerModel.fromEntries(pageData.files);

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
              _buildExplorerCard(context, explorer, pageData.files),
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
  final ValueChanged<bool?> onChanged;
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
                fontWeight: node.isDirectory ? FontWeight.w700 : FontWeight.w500,
                color: onOpen != null ? Theme.of(context).colorScheme.primary : null,
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
                      padding: const EdgeInsets.symmetric(vertical: 6, horizontal: 4),
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
  });

  final List<_ExplorerNode> roots;
  final Map<String, _ExplorerNode> nodesByPath;
  final Map<String, List<_ExplorerNode>> childrenByParent;

  factory _RecoveryExplorerModel.fromEntries(List<RecoveryFileEntry> entries) {
    final Map<String, _ExplorerNode> nodesByPath = <String, _ExplorerNode>{};
    final Map<String, String> rootLabels = <String, String>{};

    for (final RecoveryFileEntry entry in entries) {
      final String normalizedPath = _normalizePath(entry.fullPath);
      if (normalizedPath.isEmpty) {
        continue;
      }

      final String rootPath = _deriveRootPath(normalizedPath);
      rootLabels[rootPath] = _labelForRoot(rootPath);
      nodesByPath[normalizedPath] = _ExplorerNode(
        fullPath: normalizedPath,
        name: _leafName(normalizedPath),
        rootPath: rootPath,
        parentPath: null,
        isDirectory: entry.isDirectory,
        extension: entry.extension,
        sizeBytes: entry.sizeBytes,
        lastModified: entry.lastModified,
        typeLabel: _typeLabel(entry.isDirectory, entry.extension),
      );
    }

    for (final _ExplorerNode node in nodesByPath.values.toList()) {
      _ensureAncestors(nodesByPath, rootLabels, node);
    }

    for (final String rootPath in rootLabels.keys) {
      nodesByPath.putIfAbsent(
        rootPath,
        () => _ExplorerNode(
          fullPath: rootPath,
          name: rootLabels[rootPath]!,
          rootPath: rootPath,
          parentPath: null,
          isDirectory: true,
          extension: '',
          sizeBytes: 0,
          lastModified: '',
          typeLabel: 'Folder',
        ),
      );
    }

    final Map<String, _ExplorerNode> finalizedNodes = <String, _ExplorerNode>{};
    for (final _ExplorerNode node in nodesByPath.values) {
      finalizedNodes[node.fullPath] = node.copyWith(
        parentPath: _parentPathFor(node.fullPath, node.rootPath),
        name: node.fullPath == node.rootPath ? rootLabels[node.rootPath] ?? node.name : node.name,
      );
    }

    final Map<String, List<_ExplorerNode>> childrenByParent = <String, List<_ExplorerNode>>{};
    final List<_ExplorerNode> roots = <_ExplorerNode>[];

    for (final _ExplorerNode node in finalizedNodes.values) {
      if (node.fullPath == node.rootPath) {
        roots.add(node);
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

    roots.sort(_compareNodes);

    return _RecoveryExplorerModel(
      roots: roots,
      nodesByPath: finalizedNodes,
      childrenByParent: childrenByParent,
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

  static void _ensureAncestors(
    Map<String, _ExplorerNode> nodesByPath,
    Map<String, String> rootLabels,
    _ExplorerNode node,
  ) {
    final List<String> ancestors = _ancestorPaths(node.fullPath, node.rootPath);
    for (final String ancestorPath in ancestors) {
      rootLabels.putIfAbsent(node.rootPath, () => _labelForRoot(node.rootPath));
      nodesByPath.putIfAbsent(
        ancestorPath,
        () => _ExplorerNode(
          fullPath: ancestorPath,
          name: ancestorPath == node.rootPath ? _labelForRoot(node.rootPath) : _leafName(ancestorPath),
          rootPath: node.rootPath,
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

  static String _deriveRootPath(String normalizedPath) {
    final List<String> segments =
        normalizedPath.split('\\').where((String segment) => segment.isNotEmpty).toList();

    if (segments.isEmpty) {
      return normalizedPath;
    }

    if (segments.first.toUpperCase() != 'C:' && segments.first.endsWith(':')) {
      return '${segments.first.toUpperCase()}\\';
    }

    if (segments.length >= 4 &&
        segments[0].toUpperCase() == 'C:' &&
        segments[1].toLowerCase() == 'users') {
      final int folderIndex = segments[2].toLowerCase().startsWith('onedrive') ? 4 : 3;
      if (segments.length > folderIndex) {
        final String candidate = segments[folderIndex].toLowerCase();
        if (const <String>{
          'desktop',
          'documents',
          'downloads',
          'pictures',
          'videos',
          'music',
        }.contains(candidate)) {
          return segments.take(folderIndex + 1).join('\\');
        }
      }
    }

    return _driveRootFor(normalizedPath);
  }

  static String _driveRootFor(String normalizedPath) {
    final Match? match = RegExp(r'^[A-Za-z]:\\').firstMatch(normalizedPath);
    return match?.group(0)?.toUpperCase() ?? normalizedPath;
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
      return segments.first;
    }

    return segments.last;
  }

  static String? _parentPathFor(String fullPath, String rootPath) {
    if (fullPath == rootPath) {
      return null;
    }

    final String trimmed = fullPath.endsWith('\\') && fullPath.length > 3
        ? fullPath.substring(0, fullPath.length - 1)
        : fullPath;
    final int index = trimmed.lastIndexOf('\\');
    if (index <= 1) {
      return rootPath;
    }

    final String parent = trimmed.substring(0, index);
    return parent.length < rootPath.length ? rootPath : parent;
  }

  static String _leafName(String normalizedPath) {
    final String trimmed = normalizedPath.endsWith('\\') && normalizedPath.length > 3
        ? normalizedPath.substring(0, normalizedPath.length - 1)
        : normalizedPath;
    final int index = trimmed.lastIndexOf('\\');
    if (index == -1) {
      return trimmed;
    }

    return trimmed.substring(index + 1);
  }

  static String _normalizePath(String path) {
    final String trimmed = path.trim().replaceAll('/', r'\');
    if (trimmed.isEmpty) {
      return '';
    }

    final StringBuffer buffer = StringBuffer();
    var previousWasSlash = false;
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
    if (const <String>{'.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp'}.contains(ext)) {
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

    return 'Unknown';
  }
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
    String? name,
    String? parentPath,
  }) {
    return _ExplorerNode(
      fullPath: fullPath,
      name: name ?? this.name,
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
