import 'dart:typed_data';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
import '../../../devices/data/services/api_device_service.dart';
import '../../data/models/recovery_models.dart';

class FileTransferScreen extends StatefulWidget {
  const FileTransferScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  State<FileTransferScreen> createState() => _FileTransferScreenState();
}

class _FileTransferScreenState extends State<FileTransferScreen> {
  late Future<_TransferPageData> _pageFuture;
  String? _selectedDeviceId;
  String _destinationPath = '';
  RecoveryFileEntry? _selectedRecoveryFile;
  XFile? _selectedUploadFile;
  bool _isSubmitting = false;

  @override
  void initState() {
    super.initState();
    _selectedDeviceId = widget.deviceId;
    _pageFuture = _loadPageData();
  }

  Future<_TransferPageData> _loadPageData() async {
    final api = ApiDeviceService();
    final devices = await api.getDevices();

    if (devices.isEmpty) {
      return const _TransferPageData(
        devices: <dynamic>[],
        selectedDeviceId: null,
        recoveryFiles: <RecoveryFileEntry>[],
        transferHistory: <TransferJob>[],
      );
    }

    final selectedDevice = devices.firstWhere(
      (dynamic device) => '${device['id'] ?? ''}' == (_selectedDeviceId ?? ''),
      orElse: () => devices.first,
    );

    final resolvedDeviceId = '${selectedDevice['id'] ?? ''}';
    _selectedDeviceId = resolvedDeviceId;

    final results = await Future.wait<dynamic>(<Future<dynamic>>[
      api.getRecoveryFiles(resolvedDeviceId),
      api.getTransferHistory(resolvedDeviceId),
    ]);

    final recoveryFiles = (results[0] as List<RecoveryFileEntry>)
        .where((RecoveryFileEntry entry) => !entry.isDirectory)
        .toList();
    final transferHistory = results[1] as List<TransferJob>;

    if (_selectedRecoveryFile != null) {
      final String selectedPath = _selectedRecoveryFile!.fullPath;
      _selectedRecoveryFile = recoveryFiles.cast<RecoveryFileEntry?>().firstWhere(
            (RecoveryFileEntry? entry) => entry?.fullPath == selectedPath,
            orElse: () => recoveryFiles.isNotEmpty ? recoveryFiles.first : null,
          );
    } else if (recoveryFiles.isNotEmpty) {
      _selectedRecoveryFile = recoveryFiles.first;
    }

    return _TransferPageData(
      devices: devices,
      selectedDeviceId: resolvedDeviceId,
      recoveryFiles: recoveryFiles,
      transferHistory: transferHistory,
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

  Future<void> _pickUploadFile() async {
    final XFile? file = await openFile();
    if (file == null || !mounted) {
      return;
    }

    setState(() {
      _selectedUploadFile = file;
    });
  }

  Future<void> _submitUploadToDevice() async {
    final String? deviceId = _selectedDeviceId;
    final XFile? file = _selectedUploadFile;
    if (deviceId == null || file == null) {
      return;
    }

    setState(() {
      _isSubmitting = true;
    });

    try {
      final Uint8List bytes = await file.readAsBytes();
      await ApiDeviceService().uploadFileToDevice(
        deviceId: deviceId,
        destinationPath: _destinationPath,
        fileName: file.name,
        bytes: bytes,
      );

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Upload-to-device request created successfully.'),
        ),
      );
      _selectedUploadFile = null;
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
          _isSubmitting = false;
        });
      }
    }
  }

  Future<void> _submitDownloadFromDevice() async {
    final String? deviceId = _selectedDeviceId;
    final RecoveryFileEntry? file = _selectedRecoveryFile;
    if (deviceId == null || file == null) {
      return;
    }

    setState(() {
      _isSubmitting = true;
    });

    try {
      await ApiDeviceService().requestRecoveryDownload(
        deviceId: deviceId,
        filePath: file.fullPath,
      );

      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Download-from-device request created successfully.'),
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
          _isSubmitting = false;
        });
      }
    }
  }

  Future<void> _downloadCompletedTransfer(String jobId) async {
    try {
      final TransferDownload download =
          await ApiDeviceService().downloadTransferFile(jobId);
      final FileSaveLocation? location = await getSaveLocation(
        suggestedName: download.fileName,
      );
      if (location == null) {
        return;
      }

      final XFile file = XFile.fromData(
        Uint8List.fromList(download.bytes),
        name: download.fileName,
        mimeType: 'application/octet-stream',
      );
      await file.saveTo(location.path);
    } catch (error) {
      if (!mounted) {
        return;
      }

      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(error.toString())),
      );
    }
  }

  String _formatTimestamp(String value) {
    if (value.isEmpty) {
      return 'Not available';
    }

    final DateTime? parsed = DateTime.tryParse(value);
    if (parsed == null) {
      return value;
    }

    final DateTime local = parsed.toLocal();
    return '${local.year}-${local.month.toString().padLeft(2, '0')}-${local.day.toString().padLeft(2, '0')} '
        '${local.hour.toString().padLeft(2, '0')}:${local.minute.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    return AppScaffold(
      title: 'File Transfer',
      currentRoute: AppRoutes.fileTransfer,
      subtitle: 'Queue uploads to your connected device and download approved recovery files with transfer history.',
      body: FutureBuilder<_TransferPageData>(
        future: _pageFuture,
        builder: (BuildContext context, AsyncSnapshot<_TransferPageData> snapshot) {
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
                      'We could not load File Transfer',
                      style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
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

          final _TransferPageData pageData = snapshot.data ??
              const _TransferPageData(
                devices: <dynamic>[],
                selectedDeviceId: null,
                recoveryFiles: <RecoveryFileEntry>[],
                transferHistory: <TransferJob>[],
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
                      style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800),
                    ),
                    const SizedBox(height: 10),
                    const Text(
                      'Connect a Windows device first so File Transfer can queue uploads and downloads.',
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
                            _selectedRecoveryFile = null;
                            _pageFuture = _loadPageData();
                          });
                        },
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 16),
              LayoutBuilder(
                builder: (BuildContext context, BoxConstraints constraints) {
                  final bool stacked = constraints.maxWidth < 920;

                  final Widget uploadCard = Card(
                    child: Padding(
                      padding: const EdgeInsets.all(20),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: <Widget>[
                          Text(
                            'Upload To Device',
                            style: Theme.of(context).textTheme.titleLarge?.copyWith(
                                  fontWeight: FontWeight.w800,
                                ),
                          ),
                          const SizedBox(height: 10),
                          const Text(
                            'Choose a local file and queue it for the connected device. Files are saved into the device backup folder.',
                          ),
                          const SizedBox(height: 14),
                          OutlinedButton.icon(
                            onPressed: _pickUploadFile,
                            icon: const Icon(Icons.attach_file_rounded),
                            label: Text(
                              _selectedUploadFile == null
                                  ? 'Choose File'
                                  : _selectedUploadFile!.name,
                            ),
                          ),
                          const SizedBox(height: 14),
                          TextField(
                            decoration: const InputDecoration(
                              labelText: 'Destination subfolder inside backup folder',
                              border: OutlineInputBorder(),
                            ),
                            onChanged: (String value) {
                              _destinationPath = value;
                            },
                          ),
                          const SizedBox(height: 14),
                          ActionButton(
                            label: _isSubmitting ? 'Submitting...' : 'Queue Upload To Device',
                            icon: Icons.upload_rounded,
                            onPressed: _isSubmitting ? () {} : _submitUploadToDevice,
                          ),
                        ],
                      ),
                    ),
                  );

                  final Widget downloadCard = Card(
                    child: Padding(
                      padding: const EdgeInsets.all(20),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: <Widget>[
                          Text(
                            'Download From Device',
                            style: Theme.of(context).textTheme.titleLarge?.copyWith(
                                  fontWeight: FontWeight.w800,
                                ),
                          ),
                          const SizedBox(height: 10),
                          const Text(
                            'Pick an approved recovery file and queue a secure download from the connected device.',
                          ),
                          const SizedBox(height: 14),
                          if (pageData.recoveryFiles.isEmpty)
                            const Text(
                              'No recovery files found. Run the agent and sync Emergency Recovery first.',
                            )
                          else
                            DropdownButtonFormField<String>(
                              initialValue: _selectedRecoveryFile?.fullPath,
                              decoration: const InputDecoration(
                                labelText: 'Approved recovery file',
                                border: OutlineInputBorder(),
                              ),
                              items: pageData.recoveryFiles
                                  .map(
                                    (RecoveryFileEntry file) => DropdownMenuItem<String>(
                                      value: file.fullPath,
                                      child: Text(file.fileName),
                                    ),
                                  )
                                  .toList(),
                              onChanged: (String? value) {
                                setState(() {
                                  _selectedRecoveryFile = pageData.recoveryFiles.firstWhere(
                                    (RecoveryFileEntry file) => file.fullPath == value,
                                    orElse: () => pageData.recoveryFiles.first,
                                  );
                                });
                              },
                            ),
                          const SizedBox(height: 14),
                          ActionButton(
                            label: _isSubmitting ? 'Submitting...' : 'Queue Download From Device',
                            icon: Icons.download_rounded,
                            onPressed: _isSubmitting || pageData.recoveryFiles.isEmpty
                                ? () {}
                                : _submitDownloadFromDevice,
                            isPrimary: false,
                          ),
                        ],
                      ),
                    ),
                  );

                  if (stacked) {
                    return Column(
                      children: <Widget>[
                        uploadCard,
                        const SizedBox(height: 16),
                        downloadCard,
                      ],
                    );
                  }

                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Expanded(child: uploadCard),
                      const SizedBox(width: 16),
                      Expanded(child: downloadCard),
                    ],
                  );
                },
              ),
              const SizedBox(height: 16),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      Row(
                        children: <Widget>[
                          Expanded(
                            child: Text(
                              'Transfer History',
                              style: Theme.of(context).textTheme.titleLarge?.copyWith(
                                    fontWeight: FontWeight.w800,
                                  ),
                            ),
                          ),
                          ActionButton(
                            label: 'Refresh',
                            icon: Icons.refresh_rounded,
                            onPressed: _refresh,
                            isPrimary: false,
                          ),
                        ],
                      ),
                      const SizedBox(height: 12),
                      if (pageData.transferHistory.isEmpty)
                        const Text('No transfer jobs yet.')
                      else
                        ...pageData.transferHistory.map((TransferJob job) {
                          final bool canDownload =
                              job.jobType == 'download_from_device' && job.isCompleted;

                          return Padding(
                            padding: const EdgeInsets.only(bottom: 12),
                            child: Container(
                              padding: const EdgeInsets.all(14),
                              decoration: BoxDecoration(
                                borderRadius: BorderRadius.circular(16),
                                border: Border.all(color: Colors.black12),
                                color: const Color(0xFFF8FAFC),
                              ),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: <Widget>[
                                  Row(
                                    children: <Widget>[
                                      Expanded(
                                        child: Text(
                                          job.requestedFileName.isNotEmpty
                                              ? job.requestedFileName
                                              : job.storageFileName,
                                          style: const TextStyle(fontWeight: FontWeight.w700),
                                        ),
                                      ),
                                      Text(job.status),
                                    ],
                                  ),
                                  const SizedBox(height: 6),
                                  Text('Type: ${job.jobType}'),
                                  if (job.requestedFilePath.isNotEmpty)
                                    Text('Source: ${job.requestedFilePath}'),
                                  if (job.destinationPath.isNotEmpty)
                                    Text('Destination: ${job.destinationPath}'),
                                  Text('Updated: ${_formatTimestamp(job.updatedAt)}'),
                                  if (job.errorMessage.isNotEmpty)
                                    Text(
                                      'Error: ${job.errorMessage}',
                                      style: const TextStyle(color: Colors.redAccent),
                                    ),
                                  if (canDownload) ...<Widget>[
                                    const SizedBox(height: 10),
                                    ActionButton(
                                      label: 'Download Completed File',
                                      icon: Icons.download_for_offline_rounded,
                                      onPressed: () => _downloadCompletedTransfer(job.id),
                                    ),
                                  ],
                                ],
                              ),
                            ),
                          );
                        }),
                    ],
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}

class _TransferPageData {
  const _TransferPageData({
    required this.devices,
    required this.selectedDeviceId,
    required this.recoveryFiles,
    required this.transferHistory,
  });

  final List<dynamic> devices;
  final String? selectedDeviceId;
  final List<RecoveryFileEntry> recoveryFiles;
  final List<TransferJob> transferHistory;
}
