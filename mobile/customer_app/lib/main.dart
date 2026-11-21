import 'package:flutter/material.dart';

import 'app.dart';
import 'core/app_dependencies.dart';
import 'core/storage/token_storage.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(CustomerApp(dependencies: AppDependencies(storage: SecureTokenStorage())));
}
