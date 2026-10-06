// Apple's Brotli decoder (the Compression framework, libcompression) in the core's decoder contract. A header so the
// module and the macOS run of the core suite (scripts/test-ios-core.sh, ios/Tests/linux/main.m) compile the same
// function: the suite proves the decode the module ships, not a copy of it. Apple platforms only.
#import <Foundation/Foundation.h>
#include <compression.h>
#include <stddef.h>
#include <stdint.h>

static NSInteger G9SyncDataAppleBrotli(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length)
    API_AVAILABLE(ios(15.0), macos(12.0));

/**
 * `compression_decode_buffer` returns the bytes written (`capacity` when the output does not fit) and 0 on error — so
 * for `expectedLength >= 1` (capacity >= 2) a 0 is an error. For `expectedLength == 0` a 0 is ambiguous (valid empty
 * stream or error); the stream API tells the two apart: COMPRESSION_STATUS_END only comes after the stream's final
 * block.
 */
static NSInteger G9SyncDataAppleBrotli(uint8_t *destination, size_t capacity, const uint8_t *source, size_t length) {
  if (capacity > 1) {
    size_t written = compression_decode_buffer(destination, capacity, source, length, NULL, COMPRESSION_BROTLI);
    return written == 0 ? -1 : (NSInteger)written;
  }
  compression_stream stream;
  if (compression_stream_init(&stream, COMPRESSION_STREAM_DECODE, COMPRESSION_BROTLI) != COMPRESSION_STATUS_OK) return -1;
  stream.src_ptr = source;
  stream.src_size = length;
  stream.dst_ptr = destination;
  stream.dst_size = capacity;
  compression_status status = compression_stream_process(&stream, COMPRESSION_STREAM_FINALIZE);
  size_t written = capacity - stream.dst_size;
  compression_stream_destroy(&stream);
  if (written > 0) return (NSInteger)written;  // longer than the (empty) output expected
  return status == COMPRESSION_STATUS_END ? 0 : -1;
}
