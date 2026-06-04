# KubeLogViewer

> 100% AI generated

A lightweight Kubernetes log viewer for Windows, built as a companion to [OpenLens](https://github.com/MuhammedKalkan/OpenLens).

## Installation

Download the latest release from the [Releases](https://github.com/DanielMigchels/KLogScope/releases) page.

## Requirements

- Windows 10 / 11 (x64)
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or use the self-contained release build)
- Access to a Kubernetes cluster (via kubeconfig)

## Settings

Settings are stored in `%APPDATA%\KubeLogViewer\settings.json`. Delete the file to reset to defaults.

## Dependencies

- [KubernetesClient](https://github.com/kubernetes-client/csharp) 19.0.2 — official C# Kubernetes client
- .NET 10 WPF
