#import <DeviceCheck/DeviceCheck.h>

typedef void (*RevnixAttestCallback)(int requestId, const char *keyId, const char *attestation, const char *error);

extern "C" void RevnixAppAttest(int requestId, const unsigned char *clientDataHash, int length, RevnixAttestCallback callback)
{
    if (@available(iOS 14.0, *)) {
        DCAppAttestService *service = DCAppAttestService.sharedService;
        if (service.isSupported) {
            NSData *hash = [NSData dataWithBytes:clientDataHash length:length];
            [service generateKeyWithCompletionHandler:^(NSString *keyId, NSError *keyError) {
                if (keyId == nil) {
                    callback(requestId, NULL, NULL, keyError.localizedDescription.UTF8String);
                    return;
                }
                [service attestKey:keyId clientDataHash:hash completionHandler:^(NSData *attestation, NSError *attestError) {
                    if (attestation == nil) {
                        callback(requestId, NULL, NULL, attestError.localizedDescription.UTF8String);
                        return;
                    }
                    callback(requestId, keyId.UTF8String, [attestation base64EncodedStringWithOptions:0].UTF8String, NULL);
                }];
            }];
            return;
        }
    }
    callback(requestId, NULL, NULL, "App Attest unsupported");
}
