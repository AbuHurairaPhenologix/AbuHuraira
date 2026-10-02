using AutoSphere.Benchmarks;
using BenchmarkDotNet.Running;

// Micro-benchmarks for the thesis performance chapter (RQ1/RQ4). Run in Release:
//   dotnet run -c Release --project tests/AutoSphere.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(SignalCodecBenchmarks).Assembly).Run(args);
