class RecoverySettings {
  const RecoverySettings({
    required this.deviceId,
    required this.deviceName,
    required this.enabled,
    required this.approvedLocations,
    required this.lastSyncedAt,
  });

  final String deviceId;
  final String deviceName;
  final bool enabled;
  final List<RecoveryApprovedLocation> approvedLocations;
  final String lastSyncedAt;

  factory RecoverySettings.fromJson(Map<String, dynamic> json) {
    final dynamic rawLocations = json['approvedLocations'];
    final List<dynamic> locations = rawLocations is List<dynamic>
        ? rawLocations
        : <dynamic>[];

    return RecoverySettings(
      deviceId: json['deviceId']?.toString() ?? '',
      deviceName: json['deviceName']?.toString() ?? 'Unknown Device',
      enabled: json['enabled'] == true,
      approvedLocations: locations
          .whereType<Map<String, dynamic>>()
          .map(RecoveryApprovedLocation.fromJson)
          .toList(),
      lastSyncedAt: json['lastSyncedAt']?.toString() ?? '',
    );
  }

  RecoverySettings copyWith({
    String? deviceId,
    String? deviceName,
    bool? enabled,
    List<RecoveryApprovedLocation>? approvedLocations,
    String? lastSyncedAt,
  }) {
    return RecoverySettings(
      deviceId: deviceId ?? this.deviceId,
      deviceName: deviceName ?? this.deviceName,
      enabled: enabled ?? this.enabled,
      approvedLocations: approvedLocations ?? this.approvedLocations,
      lastSyncedAt: lastSyncedAt ?? this.lastSyncedAt,
    );
  }
}

class RecoveryApprovedLocation {
  const RecoveryApprovedLocation({
    required this.label,
    required this.fullPath,
    required this.driveLetter,
    required this.locationType,
  });

  final String label;
  final String fullPath;
  final String driveLetter;
  final String locationType;

  factory RecoveryApprovedLocation.fromJson(Map<String, dynamic> json) {
    return RecoveryApprovedLocation(
      label: json['label']?.toString() ?? 'Unknown',
      fullPath: json['fullPath']?.toString() ?? '',
      driveLetter: json['driveLetter']?.toString() ?? '',
      locationType: json['locationType']?.toString() ?? '',
    );
  }

  Map<String, dynamic> toJson() {
    return <String, dynamic>{
      'label': label,
      'fullPath': fullPath,
      'driveLetter': driveLetter,
      'locationType': locationType,
    };
  }
}

class RecoveryFileEntry {
  const RecoveryFileEntry({
    required this.fileName,
    required this.fullPath,
    required this.rootLabel,
    required this.rootPath,
    required this.extension,
    required this.sizeBytes,
    required this.lastModified,
    required this.isDirectory,
    required this.driveLetter,
  });

  final String fileName;
  final String fullPath;
  final String rootLabel;
  final String rootPath;
  final String extension;
  final int sizeBytes;
  final String lastModified;
  final bool isDirectory;
  final String driveLetter;

  factory RecoveryFileEntry.fromJson(Map<String, dynamic> json) {
    final dynamic sizeValue = json['sizeBytes'];

    return RecoveryFileEntry(
      fileName: json['fileName']?.toString() ?? 'Unknown',
      fullPath: json['fullPath']?.toString() ?? '',
      rootLabel: json['rootLabel']?.toString() ?? '',
      rootPath: json['rootPath']?.toString() ?? '',
      extension: json['extension']?.toString() ?? '',
      sizeBytes: sizeValue is int
          ? sizeValue
          : int.tryParse(sizeValue?.toString() ?? '') ?? 0,
      lastModified: json['lastModified']?.toString() ?? '',
      isDirectory: json['isDirectory'] == true,
      driveLetter: json['driveLetter']?.toString() ?? '',
    );
  }
}

class RecoveryInventory {
  const RecoveryInventory({
    required this.deviceId,
    required this.deviceName,
    required this.enabled,
    required this.approvedLocations,
    required this.totalFiles,
    required this.lastScanTime,
    required this.files,
  });

  final String deviceId;
  final String deviceName;
  final bool enabled;
  final List<RecoveryApprovedLocation> approvedLocations;
  final int totalFiles;
  final String lastScanTime;
  final List<RecoveryFileEntry> files;

  factory RecoveryInventory.fromJson(Map<String, dynamic> json) {
    final dynamic rawLocations = json['approvedLocations'];
    final dynamic rawFiles = json['files'];
    final dynamic totalFilesValue = json['totalFiles'];

    return RecoveryInventory(
      deviceId: json['deviceId']?.toString() ?? '',
      deviceName: json['deviceName']?.toString() ?? 'Unknown Device',
      enabled: json['enabled'] == true,
      approvedLocations: (rawLocations is List<dynamic> ? rawLocations : <dynamic>[])
          .whereType<Map<String, dynamic>>()
          .map(RecoveryApprovedLocation.fromJson)
          .toList(),
      totalFiles: totalFilesValue is int
          ? totalFilesValue
          : int.tryParse(totalFilesValue?.toString() ?? '') ?? 0,
      lastScanTime: json['lastScanTime']?.toString() ?? '',
      files: (rawFiles is List<dynamic> ? rawFiles : <dynamic>[])
          .whereType<Map<String, dynamic>>()
          .map(RecoveryFileEntry.fromJson)
          .toList(),
    );
  }
}

class TransferJob {
  const TransferJob({
    required this.id,
    required this.jobType,
    required this.status,
    required this.requestedFilePath,
    required this.requestedFileName,
    required this.destinationPath,
    required this.storageFileName,
    required this.errorMessage,
    required this.createdAt,
    required this.updatedAt,
    required this.completedAt,
  });

  final String id;
  final String jobType;
  final String status;
  final String requestedFilePath;
  final String requestedFileName;
  final String destinationPath;
  final String storageFileName;
  final String errorMessage;
  final String createdAt;
  final String updatedAt;
  final String completedAt;

  bool get isCompleted => status.toLowerCase() == 'completed';

  bool get isFailed => status.toLowerCase() == 'failed';

  factory TransferJob.fromJson(Map<String, dynamic> json) {
    return TransferJob(
      id: json['id']?.toString() ?? '',
      jobType: json['jobType']?.toString() ?? '',
      status: json['status']?.toString() ?? '',
      requestedFilePath: json['requestedFilePath']?.toString() ?? '',
      requestedFileName: json['requestedFileName']?.toString() ?? '',
      destinationPath: json['destinationPath']?.toString() ?? '',
      storageFileName: json['storageFileName']?.toString() ?? '',
      errorMessage: json['errorMessage']?.toString() ?? '',
      createdAt: json['createdAt']?.toString() ?? '',
      updatedAt: json['updatedAt']?.toString() ?? '',
      completedAt: json['completedAt']?.toString() ?? '',
    );
  }
}

class TransferDownload {
  const TransferDownload({
    required this.fileName,
    required this.bytes,
  });

  final String fileName;
  final List<int> bytes;
}
