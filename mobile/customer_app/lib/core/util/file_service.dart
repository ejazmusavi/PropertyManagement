import 'dart:io';

import 'package:open_filex/open_filex.dart';
import 'package:path_provider/path_provider.dart';

import '../network/api_client.dart';

/// Saves a downloaded file (receipt PDF, payment proof) in the app's private temporary folder and opens it with the device viewer.
abstract class FileService {
  Future<void> open(DownloadedFile file);
}

class DeviceFileService implements FileService {
  @override
  Future<void> open(DownloadedFile file) async {
    final dir = await getTemporaryDirectory();
    final safe = file.fileName.replaceAll(RegExp(r'[^A-Za-z0-9._-]'), '_');
    final target = File('${dir.path}${Platform.pathSeparator}$safe');
    await target.writeAsBytes(file.bytes, flush: true);
    final result = await OpenFilex.open(target.path);
    if (result.type != ResultType.done) {
      throw FileOpenException(result.message);
    }
  }
}

class FileOpenException implements Exception {
  FileOpenException(this.message);
  final String message;
}
