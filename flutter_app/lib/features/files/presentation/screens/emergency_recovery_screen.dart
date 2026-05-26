import 'package:flutter/material.dart';

import 'file_browser_screen.dart';

class EmergencyRecoveryScreen extends StatelessWidget {
  const EmergencyRecoveryScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  Widget build(BuildContext context) {
    return FileBrowserScreen(deviceId: deviceId);
  }
}
