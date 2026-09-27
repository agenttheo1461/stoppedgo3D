// ═══════════════════════════════════════════════════════════════════════════════
//  MAC NOTIFICATIONS -- native bridge for PlatformNotifications.cs
//
//  Exposes three C functions Unity calls via [DllImport("__Internal")] in a
//  macOS standalone build. Wraps UNUserNotificationCenter, the standard
//  macOS local-notification API.
//
//  SETUP NEEDED IN UNITY AFTER IMPORT (this file was added by hand, not
//  through Unity's own "Add Plugin" flow, so its import settings may need a
//  manual check):
//    1. Select this file in the Project window.
//    2. In the Inspector's Plugin platform settings, make sure "macOS" is
//       checked (and CPU is "Any CPU" / "x86_64 + Apple silicon" as
//       appropriate for your build).
//    3. Build as a macOS Standalone (not run from the Editor -- this native
//       code only links into an actual .app build) and confirm macOS asks
//       for notification permission on first launch. If it never asks or
//       notifications never appear: System Settings > Notifications > (your
//       app name) must be allowed, and the build should be code-signed --
//       an unsigned local build can have permission silently denied.
//
//  This was written without access to Xcode or a macOS build to verify
//  against, so if it doesn't compile/link cleanly in your build, that's the
//  most likely place something needs adjusting.
// ═══════════════════════════════════════════════════════════════════════════════

#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

extern "C" {

void _MacNotifications_RequestAuthorization()
{
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    UNAuthorizationOptions options = (UNAuthorizationOptionAlert | UNAuthorizationOptionSound | UNAuthorizationOptionBadge);
    [center requestAuthorizationWithOptions:options
                           completionHandler:^(BOOL granted, NSError * _Nullable error) {
        if (error != nil) {
            NSLog(@"[MacNotifications] requestAuthorization error: %@", error.localizedDescription);
        }
    }];
}

void _MacNotifications_Schedule(const char *identifier, const char *title, const char *body, double secondsFromNow)
{
    if (secondsFromNow <= 0) return;

    NSString *nsIdentifier = [NSString stringWithUTF8String:identifier];
    NSString *nsTitle      = [NSString stringWithUTF8String:title];
    NSString *nsBody       = [NSString stringWithUTF8String:body];

    UNMutableNotificationContent *content = [[UNMutableNotificationContent alloc] init];
    content.title = nsTitle;
    content.body  = nsBody;
    content.sound = [UNNotificationSound defaultSound];

    UNTimeIntervalNotificationTrigger *trigger =
        [UNTimeIntervalNotificationTrigger triggerWithTimeInterval:secondsFromNow repeats:NO];

    UNNotificationRequest *request =
        [UNNotificationRequest requestWithIdentifier:nsIdentifier content:content trigger:trigger];

    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center addNotificationRequest:request withCompletionHandler:^(NSError * _Nullable error) {
        if (error != nil) {
            NSLog(@"[MacNotifications] schedule error: %@", error.localizedDescription);
        }
    }];
}

void _MacNotifications_Cancel(const char *identifier)
{
    NSString *nsIdentifier = [NSString stringWithUTF8String:identifier];
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center removePendingNotificationRequestsWithIdentifiers:@[nsIdentifier]];
}

} // extern "C"
