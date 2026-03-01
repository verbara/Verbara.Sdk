using BenchmarkDotNet.Running;

// Run all benchmarks in this assembly
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
