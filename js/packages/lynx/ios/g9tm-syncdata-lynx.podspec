# @g9tm/syncdata-lynx for iOS. Linked into a host by Lynx Autolink (CocoaPods) from node_modules; the host registers
# the module on its LynxConfig: [config registerModule:G9SyncDataLynxModule.class].
# Brotli comes from Apple's Compression framework (libcompression), which decodes Brotli from iOS 15 on. The pod builds
# for iOS 12+: below 15, capabilities() reports brotli: false and brotliDecompress fails with G9SYNC-BROTLI-UNAVAILABLE.
Pod::Spec.new do |s|
  s.name = 'g9tm-syncdata-lynx'
  s.version = '1.5.10'
  s.summary = 'G9SyncDataLynxModule: Brotli decompression (Compression framework) and the snapshot-file sink (CommonCrypto SHA-256, POSIX I/O) for the G9SyncData Lynx client.'
  s.homepage = 'https://github.com/ImanKari/G9SyncData'
  s.license = { :type => 'MIT' }
  s.author = 'Iman Kari (G9TM)'
  s.platform = :ios, '15.0'
  s.source = { :path => '.' }
  s.source_files = 'src/**/*.{h,m}'
  # Keep src/'s folders in the public headers: G9SyncDataLynxModule.h imports "generated/G9SyncDataLynxModuleSpec.h", which a flattened
  # Pods/Headers/Public cannot resolve from the host app (found building an iOS host on macOS, 2026-10-06).
  s.header_mappings_dir = 'src'
  s.frameworks = 'Foundation'
  s.libraries = 'compression'
  s.requires_arc = true
  s.dependency 'Lynx', '4.0.3'
end
