# Fix My Device

A Flutter application scaffold for Windows, Android, and Web with:

- modular structure
- mock device data
- reusable widgets
- simple scalable routing
- clean professional UI

## Folder Structure

```text
lib/
  app/
    app.dart
    router/
      app_router.dart
    theme/
      app_theme.dart
  core/
    layouts/
      app_scaffold.dart
    widgets/
      action_button.dart
      device_detail_row.dart
      info_card.dart
      status_chip.dart
  features/
    auth/presentation/screens/
      login_screen.dart
    dashboard/presentation/screens/
      dashboard_screen.dart
    devices/
      data/
        models/
          device.dart
        services/
          mock_device_service.dart
      presentation/screens/
        device_details_screen.dart
        devices_list_screen.dart
    files/presentation/screens/
      file_browser_screen.dart
      file_transfer_screen.dart
    settings/presentation/screens/
      settings_screen.dart
    troubleshooting/presentation/screens/
      troubleshooting_screen.dart
  main.dart
```

## Platforms

This workspace contains the application source scaffold. If you want the full
native platform folders for Windows, Android, and Web, run:

```bash
flutter create . --platforms=windows,android,web
```

That command will preserve the `lib/` code added here and generate the missing
platform runner files.
