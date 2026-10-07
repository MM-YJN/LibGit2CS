using BenchmarkDotNet.Running;

using LibGit2CS.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, new BenchmarkConfig());
