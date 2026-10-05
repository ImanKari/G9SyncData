# @g9tm/syncdata-lynx for iOS. Linked into a host by Lynx Autolink (CocoaPods) from node_modules; the host registers
# the module on its LynxConfig: [config registerModule:G9SyncDataLynxModule.class].
# Brotli comes from Apple's Compression framework (libcompression), which decodes Brotli from iOS 15 on. The pod builds
# for iOS 12+: below 15, capabilities() reports brotli: false and brotliDecompress fails with G9SYNC-BROTLI-UNAVAILABLE.
Pod::Spec.new do |s|
  s.name = 'g9tm-syncdata-lynx'
  s.version = '1.2.1'
  s.summary = 'G9SyncDataLynxModule: Brotli decompression (Compression framework) for the G9SyncData Lynx client.'
  s.homepage = 'https://github.com/ImanKari/G9SyncData'
  s.license = { :type => 'MIT' }
  s.author = 'Iman Kari (G9TM)'
  s.platform = :ios, '15.0'
  s.source = { :path => '.' }
  s.source_files = 'src/**/*.{h,m}'
  s.frameworks = 'Foundation'
  s.libraries = 'compression'
  s.requires_arc = true
  s.dependency 'Lynx', '4.0.3'
end
