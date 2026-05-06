import 'package:flutter/material.dart';

import '../../features/devices/data/models/device.dart';

class StatusChip extends StatelessWidget {
  const StatusChip({
    required this.status,
    super.key,
  });

  final DeviceStatus status;

  @override
  Widget build(BuildContext context) {
    final ({Color background, Color foreground, String label}) styles =
        switch (status) {
      DeviceStatus.healthy => (
          background: const Color(0xFFDFF6E8),
          foreground: const Color(0xFF0F7A3D),
          label: 'Healthy',
        ),
      DeviceStatus.warning => (
          background: const Color(0xFFFFF2D8),
          foreground: const Color(0xFFB86A00),
          label: 'Warning',
        ),
      DeviceStatus.critical => (
          background: const Color(0xFFFDE2E1),
          foreground: const Color(0xFFB42318),
          label: 'Critical',
        ),
      DeviceStatus.offline => (
          background: const Color(0xFFE9EDF2),
          foreground: const Color(0xFF52606D),
          label: 'Offline',
        ),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: styles.background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        styles.label,
        style: TextStyle(
          color: styles.foreground,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}
