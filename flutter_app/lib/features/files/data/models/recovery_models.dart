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
}

class RecoveryFileEntry {
  const RecoveryFileEntry({
    required this.fileName,
    required this.fullPath,
    required this.extension,
    required this.sizeBytes,
    required this.lastModified,
    required this.isDirectory,
    required this.driveLetter,
  });

  final String fileName;
  final String fullPath;
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
