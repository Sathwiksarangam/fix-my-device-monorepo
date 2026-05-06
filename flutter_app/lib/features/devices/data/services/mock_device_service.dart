import '../models/device.dart';

class MockDeviceService {
  static const List<Device> _devices = <Device>[
    Device(
      id: 'DV-1001',
      name: 'Front Desk Surface',
      owner: 'Sarah Johnson',
      platform: 'Windows 11',
      location: 'New York Office',
      ipAddress: '10.0.1.24',
      storageUsed: 312,
      storageTotal: 512,
      batteryHealth: 91,
      lastSeen: '2 minutes ago',
      status: DeviceStatus.healthy,
      notes: 'All services responding normally.',
    ),
    Device(
      id: 'DV-1002',
      name: 'Warehouse Scanner',
      owner: 'Miguel Rivera',
      platform: 'Android 14',
      location: 'Warehouse A',
      ipAddress: '10.0.2.48',
      storageUsed: 54,
      storageTotal: 128,
      batteryHealth: 74,
      lastSeen: '8 minutes ago',
      status: DeviceStatus.warning,
      notes: 'Battery wear is increasing and sync latency is elevated.',
    ),
    Device(
      id: 'DV-1003',
      name: 'Support Kiosk',
      owner: 'IT Operations',
      platform: 'Windows 10',
      location: 'Lobby',
      ipAddress: '10.0.3.12',
      storageUsed: 451,
      storageTotal: 512,
      batteryHealth: 63,
      lastSeen: '12 minutes ago',
      status: DeviceStatus.critical,
      notes: 'Storage nearly full and remote diagnostics failed twice.',
    ),
    Device(
      id: 'DV-1004',
      name: 'Field Tablet 7',
      owner: 'Alex Chen',
      platform: 'Android 13',
      location: 'Remote Site',
      ipAddress: 'Unavailable',
      storageUsed: 77,
      storageTotal: 128,
      batteryHealth: 88,
      lastSeen: '3 hours ago',
      status: DeviceStatus.offline,
      notes: 'Last known state was stable before connection loss.',
    ),
  ];

  List<Device> getDevices() {
    return _devices;
  }

  Device getDeviceById(String? id) {
    final List<Device> devices = getDevices();
    return devices.firstWhere(
      (Device device) => device.id == id,
      orElse: () => devices.first,
    );
  }

  List<String> getMockFiles(String? deviceId) {
    final Device device = getDeviceById(deviceId);

    return <String>[
      '/${device.name}/Logs/diagnostics.log',
      '/${device.name}/Logs/system_events.log',
      '/${device.name}/Backups/config_backup.zip',
      '/${device.name}/Screenshots/latest_issue.png',
      '/${device.name}/Reports/device_health_report.pdf',
    ];
  }

  List<String> getSuggestedActions(String? deviceId) {
    final Device device = getDeviceById(deviceId);

    switch (device.status) {
      case DeviceStatus.healthy:
        return <String>[
          'Run scheduled health report',
          'Review recent file activity',
          'Confirm backup policy status',
        ];
      case DeviceStatus.warning:
        return <String>[
          'Inspect battery usage trends',
          'Clear temporary files',
          'Validate sync service credentials',
        ];
      case DeviceStatus.critical:
        return <String>[
          'Free storage immediately',
          'Collect crash logs',
          'Escalate to senior support technician',
        ];
      case DeviceStatus.offline:
        return <String>[
          'Check network availability',
          'Confirm device power state',
          'Retry remote connection in 15 minutes',
        ];
    }
  }
}
