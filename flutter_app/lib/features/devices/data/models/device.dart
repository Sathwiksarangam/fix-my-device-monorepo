enum DeviceStatus {
  healthy,
  warning,
  critical,
  offline,
}

class Device {
  const Device({
    required this.id,
    required this.name,
    required this.owner,
    required this.platform,
    required this.location,
    required this.ipAddress,
    required this.storageUsed,
    required this.storageTotal,
    required this.batteryHealth,
    required this.lastSeen,
    required this.status,
    required this.notes,
  });

  final String id;
  final String name;
  final String owner;
  final String platform;
  final String location;
  final String ipAddress;
  final int storageUsed;
  final int storageTotal;
  final int batteryHealth;
  final String lastSeen;
  final DeviceStatus status;
  final String notes;

  bool get isOnline => status != DeviceStatus.offline;
  bool get needsAttention =>
      status == DeviceStatus.warning || status == DeviceStatus.critical;

  double get storageProgress => storageUsed / storageTotal;

  String get storageLabel => '$storageUsed GB / $storageTotal GB';
}
