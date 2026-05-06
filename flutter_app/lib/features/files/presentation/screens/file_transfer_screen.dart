import 'package:flutter/material.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/action_button.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../devices/data/services/mock_device_service.dart';

class FileTransferScreen extends StatelessWidget {
  const FileTransferScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  Widget build(BuildContext context) {
    final device = MockDeviceService().getDeviceById(deviceId);

    return AppScaffold(
      title: 'File Transfer',
      currentRoute: AppRoutes.fileTransfer,
      subtitle: 'Stage mock upload and download jobs with clean, reusable transfer controls.',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          InfoCard(
            title: 'Transfer Target',
            value: device.name,
            subtitle: 'Prepare secure upload and download tasks',
            icon: Icons.swap_horizontal_circle_rounded,
          ),
          const SizedBox(height: 20),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(18),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  const TextField(
                    decoration: InputDecoration(
                      labelText: 'Source path',
                      prefixIcon: Icon(Icons.drive_folder_upload_rounded),
                    ),
                  ),
                  const SizedBox(height: 14),
                  const TextField(
                    decoration: InputDecoration(
                      labelText: 'Destination path',
                      prefixIcon: Icon(Icons.folder_zip_rounded),
                    ),
                  ),
                  const SizedBox(height: 18),
                  Row(
                    children: <Widget>[
                      Expanded(
                        child: ActionButton(
                          label: 'Upload File',
                          icon: Icons.upload_rounded,
                          onPressed: () {},
                        ),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: ActionButton(
                          label: 'Download File',
                          icon: Icons.download_rounded,
                          onPressed: () {},
                          isPrimary: false,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 20),
          Wrap(
            spacing: 16,
            runSpacing: 16,
            children: const <Widget>[
              SizedBox(
                width: 280,
                child: InfoCard(
                  title: 'Queued Transfers',
                  value: '03',
                  subtitle: '2 uploads and 1 download pending',
                  icon: Icons.pending_actions_rounded,
                ),
              ),
              SizedBox(
                width: 280,
                child: InfoCard(
                  title: 'Transfer Mode',
                  value: 'Secure Mock',
                  subtitle: 'Placeholder workflow for future API integration',
                  icon: Icons.verified_user_outlined,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
