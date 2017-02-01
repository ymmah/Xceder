This is to simplify the client side development. It will handle the protobuf
message protocol and network communication. The client side only need call
the provided functions to submit the request and receive the real time data
through the events.

# Project Setup
	
	in the VS IDE can just simply restore the NuGet packages in the solution popup menu by right mouse click

1. Install the google protobuf package (Google.Protobuf) through NuGet package manager
2. Install the google protobuf tools package (Google.Protobuf.Tools) through NuGet package manager  
3. Install the microsoft reactive package (System.Reactive) through NuGet package manager  
4. Configure the project build events to generate the protobuf message classes in project "Build Events\Pre-Build event command line"

		"$(USERPROFILE)packages\.nuget\Google.Protobuf.Tools\3.2.0\tools\windows_x64\protoc.exe"  --proto_path="$(ProjectDir).."  --csharp_out=$(ProjectDir)protobuf  $(ProjectDir)..\gateway.proto
	
# Folders

* protobuf

  store the generated protobuf messages classes from the gateway.proto
  and tools to parse the protobuf messages from the network stream

* packages
	
  store the installed packages through NuGet package manager








