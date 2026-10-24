using Deadwax.Drive;

namespace Deadwax.Cli;

/// "Make best copy" (spec §8) on reads already made: raw passes of one track,
/// as `deadwax read --save` keeps them, voted into one (BestCopy). For trying
/// the vote on a damaged disc's reads without reading it again.
internal static class BestCopyCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) return Task.FromResult(Commands.Fail("usage: deadwax best-copy OUT.raw READ1.raw READ2.raw [READ3.raw ...]"));
        var reads = args[1..].Select(f => File.ReadAllBytes(Commands.Home(f))).ToList();
        var result = BestCopy.Vote(reads);
        File.WriteAllBytes(Commands.Home(args[0]), result.Audio);
        Console.WriteLine($"{result.Sectors} sectors from {reads.Count} reads: {result.Unanimous} alike in every read, " +
                          $"{result.Voted} decided by a majority, {result.NoMajority} with no majority anywhere (the last read's kept there).");
        return Task.FromResult(0);
    }
}
