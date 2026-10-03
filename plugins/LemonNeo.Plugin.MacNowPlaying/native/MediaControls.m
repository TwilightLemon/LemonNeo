#import <AppKit/AppKit.h>
#import <MediaPlayer/MediaPlayer.h>
#include <stdio.h>

// Enum values mirror InteractiveContracts.cs. Only JSON crosses the process boundary.
enum { ControlPlay = 1, ControlPause = 2, ControlStop = 4,
       ControlNext = 8, ControlPrevious = 16, ControlSeek = 32 };

static void Emit(NSDictionary *message) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:message options:0 error:nil];
    @synchronized(NSFileHandle.fileHandleWithStandardOutput) {
        fwrite(data.bytes, 1, data.length, stdout);
        fputc('\n', stdout);
        fflush(stdout);
    }
}

@interface MediaControls : NSObject
@property(nonatomic, strong) NSMutableArray *targets;
@property(nonatomic, strong) NSMutableDictionary *info;
@property(nonatomic, copy) NSString *artworkURL;
@property(nonatomic, strong) NSURLSessionDataTask *artworkTask;
@property(nonatomic) NSUInteger artworkGeneration;
- (void)update:(NSDictionary *)snapshot;
- (void)shutdown;
@end

@implementation MediaControls
- (instancetype)init {
    self = [super init];
    if (!self) return nil;
    self.targets = [NSMutableArray array];
    self.info = [NSMutableDictionary dictionary];
    MPRemoteCommandCenter *center = MPRemoteCommandCenter.sharedCommandCenter;
    NSArray *commands = @[center.playCommand, center.pauseCommand, center.togglePlayPauseCommand,
                          center.stopCommand, center.nextTrackCommand, center.previousTrackCommand,
                          center.changePlaybackPositionCommand];
    for (NSUInteger index = 0; index < commands.count; index++) {
        MPRemoteCommand *command = commands[index];
        command.enabled = NO;
        id token = [command addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent *event) {
            NSMutableDictionary *message = [@{@"command": @(index)} mutableCopy];
            if (index == 6) message[@"position"] = @(((MPChangePlaybackPositionCommandEvent *)event).positionTime);
            Emit(message);
            return MPRemoteCommandHandlerStatusSuccess;
        }];
        [self.targets addObject:@[command, token]];
    }
    return self;
}

- (void)update:(NSDictionary *)snapshot {
    MPNowPlayingInfoCenter *infoCenter = MPNowPlayingInfoCenter.defaultCenter;
    MPRemoteCommandCenter *commands = MPRemoteCommandCenter.sharedCommandCenter;
    NSInteger controls = [snapshot[@"controls"] integerValue];
    commands.playCommand.enabled = (controls & ControlPlay) != 0;
    commands.pauseCommand.enabled = (controls & ControlPause) != 0;
    commands.togglePlayPauseCommand.enabled = (controls & (ControlPlay | ControlPause)) != 0;
    commands.stopCommand.enabled = (controls & ControlStop) != 0;
    commands.nextTrackCommand.enabled = (controls & ControlNext) != 0;
    commands.previousTrackCommand.enabled = (controls & ControlPrevious) != 0;
    commands.changePlaybackPositionCommand.enabled = (controls & ControlSeek) != 0;

    if (snapshot[@"title"] == NSNull.null) {
        [self.artworkTask cancel];
        self.artworkTask = nil;
        self.artworkGeneration++;
        self.artworkURL = nil;
        [self.info removeAllObjects];
        infoCenter.nowPlayingInfo = nil;
        infoCenter.playbackState = MPNowPlayingPlaybackStateStopped;
        return;
    }

    NSMutableDictionary *info = [NSMutableDictionary dictionary];
    info[MPMediaItemPropertyTitle] = snapshot[@"title"];
    if (snapshot[@"artist"] != NSNull.null) info[MPMediaItemPropertyArtist] = snapshot[@"artist"];
    if (snapshot[@"album"] != NSNull.null) info[MPMediaItemPropertyAlbumTitle] = snapshot[@"album"];
    if (snapshot[@"duration"] != NSNull.null) info[MPMediaItemPropertyPlaybackDuration] = snapshot[@"duration"];
    if (snapshot[@"trackId"] != NSNull.null) info[MPNowPlayingInfoPropertyExternalContentIdentifier] = snapshot[@"trackId"];
    NSInteger state = [snapshot[@"state"] integerValue];
    info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = snapshot[@"position"];
    info[MPNowPlayingInfoPropertyPlaybackRate] = @(state == 1 ? 1.0 : 0.0);
    info[MPNowPlayingInfoPropertyDefaultPlaybackRate] = @1.0;
    info[MPNowPlayingInfoPropertyMediaType] = @(MPNowPlayingInfoMediaTypeAudio);
    NSString *artworkURL = snapshot[@"artwork"] == NSNull.null ? nil : snapshot[@"artwork"];
    BOOL sameArtwork = (artworkURL == nil && self.artworkURL == nil) || [artworkURL isEqualToString:self.artworkURL];
    if (sameArtwork && self.info[MPMediaItemPropertyArtwork])
        info[MPMediaItemPropertyArtwork] = self.info[MPMediaItemPropertyArtwork];
    self.info = info;
    infoCenter.nowPlayingInfo = info;
    infoCenter.playbackState = state == 1 ? MPNowPlayingPlaybackStatePlaying :
                              state == 2 ? MPNowPlayingPlaybackStatePaused : MPNowPlayingPlaybackStateStopped;
    if (!sameArtwork) {
        [self.artworkTask cancel];
        self.artworkTask = nil;
        self.artworkURL = artworkURL;
        NSUInteger generation = ++self.artworkGeneration;
        NSURL *url = artworkURL ? [NSURL URLWithString:artworkURL] : nil;
        if (url && ([url.scheme isEqualToString:@"https"] || [url.scheme isEqualToString:@"http"] || url.isFileURL)) {
            // Load off the main run loop; a generation check prevents an old
            // request from applying its cover after a track change or clearing.
            __weak MediaControls *weakSelf = self;
            void (^publish)(NSData *) = ^(NSData *data) {
                NSImage *image = data ? [[NSImage alloc] initWithData:data] : nil;
                if (!image) return;
                dispatch_async(dispatch_get_main_queue(), ^{
                    MediaControls *owner = weakSelf;
                    if (!owner || owner.artworkGeneration != generation) return;
                    MPMediaItemArtwork *artwork = [[MPMediaItemArtwork alloc] initWithBoundsSize:image.size
                        requestHandler:^NSImage *(CGSize size) { return image; }];
                    owner.info[MPMediaItemPropertyArtwork] = artwork;
                    MPNowPlayingInfoCenter.defaultCenter.nowPlayingInfo = owner.info;
                });
            };
            if (url.isFileURL) {
                dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
                    publish([NSData dataWithContentsOfURL:url]);
                });
            } else {
                NSMutableURLRequest *request = [NSMutableURLRequest requestWithURL:url];
                request.timeoutInterval = 10;
                self.artworkTask = [NSURLSession.sharedSession dataTaskWithRequest:request
                    completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
                        if (!error && (![response isKindOfClass:NSHTTPURLResponse.class] ||
                            ((NSHTTPURLResponse *)response).statusCode == 200)) publish(data);
                    }];
                [self.artworkTask resume];
            }
        }
    }
}

- (void)shutdown {
    [self.artworkTask cancel];
    self.artworkGeneration++;
    for (NSArray *pair in self.targets) {
        MPRemoteCommand *command = pair[0];
        command.enabled = NO;
        [command removeTarget:pair[1]];
    }
    [self.targets removeAllObjects];
    MPNowPlayingInfoCenter.defaultCenter.nowPlayingInfo = nil;
    MPNowPlayingInfoCenter.defaultCenter.playbackState = MPNowPlayingPlaybackStateStopped;
}
@end

int main(void) {
    @autoreleasepool {
        NSApplication *application = NSApplication.sharedApplication;
        [application setActivationPolicy:NSApplicationActivationPolicyProhibited];
        MediaControls *controls = [[MediaControls alloc] init];
        Emit(@{@"ready": @YES});
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
            char *buffer = NULL;
            size_t capacity = 0;
            ssize_t length;
            while ((length = getline(&buffer, &capacity, stdin)) > 0) {
                @autoreleasepool {
                    NSData *data = [NSData dataWithBytes:buffer length:(NSUInteger)length];
                    NSDictionary *snapshot = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
                    if ([snapshot isKindOfClass:NSDictionary.class])
                        dispatch_async(dispatch_get_main_queue(), ^{ [controls update:snapshot]; });
                }
            }
            free(buffer);
            dispatch_async(dispatch_get_main_queue(), ^{
                [controls shutdown];
                [application terminate:nil];
            });
        });
        [application run];
    }
    return 0;
}
