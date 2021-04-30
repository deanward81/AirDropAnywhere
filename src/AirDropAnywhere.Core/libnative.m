/*
 * Code derived from https://github.com/yggdrasil-network/yggdrasil-go/blob/master/src/multicast/multicast_darwin.go
 * Forces the system to initialize AWDL on MacOS so that we can advertise AirDrop
 * services using it.
 */
#import <Foundation/Foundation.h>
NSNetServiceBrowser *serviceBrowser;
void StartAWDLBrowsing() {
	if (serviceBrowser == nil) {
		serviceBrowser = [[NSNetServiceBrowser alloc] init];
		serviceBrowser.includesPeerToPeer = YES;
	}
	[serviceBrowser searchForServicesOfType:@"_airdrop_proxy._tcp" inDomain:@""];
}
void StopAWDLBrowsing() {
	if (serviceBrowser == nil) {
		return;
	}
	[serviceBrowser stop];
}