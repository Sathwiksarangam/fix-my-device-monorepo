import 'package:flutter/material.dart';

import 'app/app.dart';
import 'features/auth/data/auth_service.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await AuthService.initializeSession();
  runApp(const FixMyDeviceApp());
}
