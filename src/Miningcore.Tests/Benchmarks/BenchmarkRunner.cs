using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Miningcore.Tests.Benchmarks.Stratum;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable 8974

namespace Miningcore.Tests.Benchmarks;


public class Benchmarks
{
    private readonly ITestOutputHelper output;

    public Benchmarks(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Skip = "uncomment me to run")]
    public void Run_Benchmarks()
    {
        var logger = new AccumulationLogger();

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddLogger(logger)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkRunner.Run<StratumConnectionBenchmarks>(config);

        // write benchmark summary
        output.WriteLine(logger.GetLog());
    }
}
