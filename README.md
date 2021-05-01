# AirDropAnywhere

> **NOTE** This project is still a work in progress - some of the details below are the intended end state of the project, not the current state! More information can be found at my blog at https://bakedbean.org.uk/tags/airdrop/

A .NET Core implementation of the AirDrop protocol that allows arbitrary devices to send/receive files from devices that support AirDrop natively (i.e. Apple devices).

## Overview

AirDropAnywhere implements the mDNS listener and HTTP API needed to handle the AirDrop protocol in such a way that they can be consumed by any application. However, in order to be able to successfully use AirDrop the hardware executing these components needs to support Apple Wireless Direct Link (AWDL) or Open Wireless Link ([OWL](https://owlink.org/)) - that usually means an Apple device or Linux with a wireless interface that supports RFMON.

It also exposes a web server that serves up a website that can be used by arbitrary devices that do not support AirDrop natively to send/receive files to/from devices that do support AirDrop.

## Structure

AirDropAnywhere is split into several projects:

`AirDropAnywhere.Core` contains all our shared services (e.g. mDNS listener, core AirDrop HTTP API) and the means to configure them in a `WebHost`. This is exposed as a NuGet package called `AirDropAnywhere`.

`AirDropAnywhere.Cli` is a CLI application hosting the services and rendered using [Spectre.Console](https://github.com/spectreconsole/spectre.console). It'll typically be used as a way to send / receive for the current machine via the command line.

`AirDropAnywhere.Web` hosts the services and exposes a website that any device in the same network can connect to and be able to send/receive files to/from AirDrop-compatible devices. It makes use of Vue.js for its UI and SignalR for realtime communication needed for the backend to function.

## Components

### mDNS
mDNS is implemented as an `IHostedService` that executes as part of the `WebHost`. Its sole purpose is to dynamically advertise each device that wants to send/receive files using AirDrop. It listens on IPv6 and IPv4 multicast on UDP 5353 and responds to mDNS queries over the AWDL interface exposed by Apple hardware or OWL's virtual interface.

Bulk of the implementation is in the [MulticastDns](https://github.com/deanward81/AirDropAnywhere/tree/main/src/AirDropAnywhere.Core/MulticastDns) folder.

### HTTP API
AirDrop is implemented as an HTTP API that ties into Kestrel using endpoint routing (i.e. no MVC, etc.). [AirDropRouteHandler](https://github.com/deanward81/AirDropAnywhere/tree/main/src/AirDropAnywhere.Core/AirDropRouteHandler.cs) implements the protocol.

_TODO_ finish README!