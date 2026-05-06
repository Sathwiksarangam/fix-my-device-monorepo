import 'package:flutter/material.dart';

import '../../../../app/router/app_router.dart';
import '../../../../core/layouts/app_scaffold.dart';
import '../../../../core/widgets/info_card.dart';
import '../../../devices/data/services/mock_device_service.dart';

class FileBrowserScreen extends StatelessWidget {
  const FileBrowserScreen({
    super.key,
    this.deviceId,
  });

  final String? deviceId;

  @override
  Widget build(BuildContext context) {
    final service = MockDeviceService();
    final device = service.getDeviceById(deviceId);
    final files = service.getMockFiles(deviceId);

    return AppScaffold(
      title: 'File Browser',
      currentRoute: AppRoutes.fileBrowser,
      subtitle: 'Inspect mock device storage paths before upload, download, or diagnostics collection.',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          InfoCard(
            title: 'Connected Device',
            value: device.name,
            subtitle: 'Mock file system view for ${device.platform}',
            icon: Icons.folder_copy_rounded,
          ),
          const SizedBox(height: 20),
          ...files.map(
            (filePath) => Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: Card(
                child: ListTile(
                  leading: CircleAvatar(
                    backgroundColor: Theme.of(context).colorScheme.primaryContainer,
                    child: Icon(
                      filePath.endsWith('.zip')
                          ? Icons.archive_outlined
                          : filePath.endsWith('.png')
                              ? Icons.image_outlined
                              : filePath.endsWith('.pdf')
                                  ? Icons.picture_as_pdf_outlined
                                  : Icons.insert_drive_file_outlined,
                      color: Theme.of(context).colorScheme.primary,
                    ),
                  ),
                  title: Text(filePath.split('/').last),
                  subtitle: Text(filePath),
                  trailing: const Icon(Icons.chevron_right_rounded),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
