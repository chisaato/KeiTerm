#import <Foundation/Foundation.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

typedef enum {
    KeiSuccess = 0,
    KeiCancelled = 1,
    KeiUnavailable = 2,
    KeiNotEnrolled = 3,
    KeiFailed = 4
} KeiOutcome;

static NSString *const KeiKeychainService = @"com.kei.term.vault.quick-unlock.v1";
static const int KeiKeyLength = 32;

static KeiOutcome KeiAuthenticationOutcome(NSError *error) {
    if (![error.domain isEqualToString:LAErrorDomain])
        return KeiFailed;
    switch (error.code) {
        case LAErrorUserCancel:
        case LAErrorUserFallback:
        case LAErrorSystemCancel:
        case LAErrorAppCancel:
            return KeiCancelled;
        case LAErrorBiometryNotEnrolled:
            return KeiNotEnrolled;
        case LAErrorBiometryNotAvailable:
        case LAErrorBiometryLockout:
        case LAErrorNotInteractive:
            return KeiUnavailable;
        default:
            return KeiFailed;
    }
}

static KeiOutcome KeiKeychainOutcome(OSStatus status) {
    if (status == errSecSuccess)
        return KeiSuccess;
    if (status == errSecUserCanceled)
        return KeiCancelled;
    if (status == errSecNotAvailable || status == errSecInteractionNotAllowed)
        return KeiUnavailable;
    return KeiFailed;
}

static NSMutableDictionary *KeiQuery(NSString *account) {
    // 不设置 Data Protection Keychain 或 synchronizable，使用本机当前用户的传统钥匙串。
    return [@{ (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService: KeiKeychainService,
        (__bridge id)kSecAttrAccount: account,
        (__bridge id)kSecAttrSynchronizable: @NO } mutableCopy];
}

static NSString *KeiAccount(const char *keyId) {
    if (keyId == NULL)
        return nil;
    NSString *account = [NSString stringWithUTF8String:keyId];
    return account.length > 0 && account.length <= 256 ? account : nil;
}

static void KeiErase(void *bytes, size_t length) {
    // volatile 防止编译器把释放前的清零当作无用写入移除。
    volatile unsigned char *cursor = bytes;
    while (length-- > 0)
        *cursor++ = 0;
}

int kei_quick_unlock_available(void) {
    @autoreleasepool {
        LAContext *context = [[LAContext alloc] init];
        NSError *error = nil;
        BOOL available = [context canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:&error];
        [context invalidate];
        return available ? KeiSuccess : KeiAuthenticationOutcome(error);
    }
}

int kei_quick_unlock_has_key(const char *keyId, int *hasKey) {
    @autoreleasepool {
        NSString *account = KeiAccount(keyId);
        if (account == nil || hasKey == NULL)
            return KeiFailed;
        *hasKey = 0;
        NSMutableDictionary *query = KeiQuery(account);
        query[(__bridge id)kSecReturnAttributes] = @YES;
        query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
        // 属性查询不请求秘密，避免设置页刷新时触发生物验证。
        CFTypeRef attributes = NULL;
        OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &attributes);
        if (attributes != NULL)
            CFRelease(attributes);
        if (status == errSecItemNotFound)
            return KeiSuccess;
        *hasKey = status == errSecSuccess;
        return KeiKeychainOutcome(status);
    }
}

int kei_quick_unlock_store(const char *keyId, const unsigned char *key, int length) {
    @autoreleasepool {
        NSString *account = KeiAccount(keyId);
        if (account == nil || key == NULL || length != KeiKeyLength)
            return KeiFailed;
        NSMutableData *material = [NSMutableData dataWithBytes:key length:(NSUInteger)length];
        NSMutableDictionary *query = KeiQuery(account);
        NSDictionary *attributes = @{ (__bridge id)kSecValueData: material,
            (__bridge id)kSecAttrLabel: @"KeiTerm 本机快速解锁" };
        OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query, (__bridge CFDictionaryRef)attributes);
        if (status == errSecItemNotFound) {
            [query addEntriesFromDictionary:attributes];
            status = SecItemAdd((__bridge CFDictionaryRef)query, NULL);
        }
        KeiErase(material.mutableBytes, material.length);
        return KeiKeychainOutcome(status);
    }
}

int kei_quick_unlock_delete(const char *keyId) {
    @autoreleasepool {
        NSString *account = KeiAccount(keyId);
        if (account == nil)
            return KeiFailed;
        OSStatus status = SecItemDelete((__bridge CFDictionaryRef)KeiQuery(account));
        return status == errSecItemNotFound ? KeiSuccess : KeiKeychainOutcome(status);
    }
}

@interface KeiUnlockRequest : NSObject {
    LAContext *_context;
    NSString *_account;
    NSString *_reason;
    NSLock *_lock;
    dispatch_semaphore_t _completed;
    BOOL _cancelled;
    BOOL _started;
    BOOL _finished;
    KeiOutcome _outcome;
    void *_key;
    int _keyLength;
}
- (instancetype)initWithAccount:(NSString *)account reason:(NSString *)reason;
- (KeiOutcome)wait;
- (void)cancel;
- (KeiOutcome)getKey:(const void **)key length:(int *)length;
@end

@implementation KeiUnlockRequest
- (instancetype)initWithAccount:(NSString *)account reason:(NSString *)reason {
    self = [super init];
    if (self != nil) {
        _context = [[LAContext alloc] init];
        // 主密码回退由 KeiTerm 提供，不让系统生物策略改成系统密码验证。
        _context.localizedFallbackTitle = @"";
        _account = account;
        _reason = reason;
        _lock = [[NSLock alloc] init];
        _completed = dispatch_semaphore_create(0);
        _outcome = KeiFailed;
    }
    return self;
}

- (void)dealloc {
    [_context invalidate];
    if (_key != NULL) {
        KeiErase(_key, (size_t)_keyLength);
        free(_key);
    }
}

- (void)cancel {
    [_lock lock];
    _cancelled = YES;
    [_lock unlock];
    [_context invalidate];
    dispatch_semaphore_signal(_completed);
}

- (void)finishWithSuccess:(BOOL)success error:(NSError *)error {
    @autoreleasepool {
        [_lock lock];
        BOOL cancelled = _cancelled;
        [_lock unlock];
        KeiOutcome outcome = cancelled ? KeiCancelled : KeiAuthenticationOutcome(error);
        void *key = NULL;
        int keyLength = 0;
        if (success && !cancelled) {
            NSMutableDictionary *query = KeiQuery(_account);
            query[(__bridge id)kSecReturnData] = @YES;
            query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
            _context.interactionNotAllowed = YES;
            query[(__bridge id)kSecUseAuthenticationContext] = _context;
            CFTypeRef result = NULL;
            OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
            outcome = status == errSecItemNotFound ? KeiNotEnrolled : KeiKeychainOutcome(status);
            if (status == errSecSuccess && result != NULL && CFGetTypeID(result) == CFDataGetTypeID() &&
                CFDataGetLength((CFDataRef)result) == KeiKeyLength) {
                keyLength = KeiKeyLength;
                key = malloc((size_t)keyLength);
                if (key != NULL)
                    CFDataGetBytes((CFDataRef)result, CFRangeMake(0, keyLength), key);
                else
                    outcome = KeiFailed;
            } else if (status == errSecSuccess) {
                outcome = KeiFailed;
            }
            if (result != NULL)
                CFRelease(result);
        }
        [_lock lock];
        if (_cancelled) {
            outcome = KeiCancelled;
            if (key != NULL) {
                KeiErase(key, (size_t)keyLength);
                free(key);
                key = NULL;
                keyLength = 0;
            }
        }
        _key = key;
        _keyLength = keyLength;
        _outcome = outcome;
        _finished = YES;
        [_lock unlock];
        dispatch_semaphore_signal(_completed);
    }
}

- (KeiOutcome)wait {
    [_lock lock];
    if (_cancelled || _started) {
        KeiOutcome outcome = _cancelled ? KeiCancelled : KeiFailed;
        [_lock unlock];
        return outcome;
    }
    _started = YES;
    [_lock unlock];
    NSError *error = nil;
    if (![_context canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:&error]) {
        [_lock lock];
        KeiOutcome outcome = _cancelled ? KeiCancelled : KeiAuthenticationOutcome(error);
        [_lock unlock];
        return outcome;
    }
    // reply block 强引用 self：托管方取消并释放句柄后，迟到回调仍然安全，并在完成时擦除材料。
    [_context evaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics localizedReason:_reason
        reply:^(BOOL success, NSError *replyError) {
            // 传统钥匙串读取可能等待 ACL / 钥匙串授权，不占用 LocalAuthentication 的私有回复队列。
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
                [self finishWithSuccess:success error:replyError];
            });
        }];
    dispatch_semaphore_wait(_completed, DISPATCH_TIME_FOREVER);
    [_lock lock];
    KeiOutcome outcome = _cancelled ? KeiCancelled : (_finished ? _outcome : KeiFailed);
    [_lock unlock];
    return outcome;
}

- (KeiOutcome)getKey:(const void **)key length:(int *)length {
    if (key == NULL || length == NULL)
        return KeiFailed;
    *key = NULL;
    *length = 0;
    [_lock lock];
    KeiOutcome outcome = _cancelled ? KeiCancelled : (_finished ? _outcome : KeiFailed);
    if (outcome == KeiSuccess) {
        *key = _key;
        *length = _keyLength;
    }
    [_lock unlock];
    return outcome;
}
@end

void *kei_quick_unlock_request_create(const char *keyId, const char *reason) {
    @autoreleasepool {
        NSString *account = KeiAccount(keyId);
        NSString *message = reason == NULL ? nil : [NSString stringWithUTF8String:reason];
        if (account == nil || message.length == 0)
            return NULL;
        return (__bridge_retained void *)[[KeiUnlockRequest alloc] initWithAccount:account reason:message];
    }
}

int kei_quick_unlock_request_wait(void *request) {
    @autoreleasepool {
        return request == NULL ? KeiFailed : [(__bridge KeiUnlockRequest *)request wait];
    }
}

void kei_quick_unlock_request_cancel(void *request) {
    @autoreleasepool {
        if (request != NULL)
            [(__bridge KeiUnlockRequest *)request cancel];
    }
}

int kei_quick_unlock_request_get_key(void *request, const void **key, int *length) {
    @autoreleasepool {
        return request == NULL ? KeiFailed : [(__bridge KeiUnlockRequest *)request getKey:key length:length];
    }
}

void kei_quick_unlock_request_release(void *request) {
    @autoreleasepool {
        if (request != NULL)
            CFRelease(request);
    }
}
