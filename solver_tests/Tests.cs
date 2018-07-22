using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class Tests
{
    [TestMethod]
    public void TestFundamentals()
    {
        foreach (var testCase in Enumerable.Range(1, 20))
        {
            var target = Model.Load(File.ReadAllBytes($"{root}\\problems\\LA{testCase:D3}_tgt.mdl"));

            var traceBytes = File.ReadAllBytes($"{root}\\solutions\\LA{testCase:D3}.nbt");

            var trace = Command.GetCommands(traceBytes);

            var state = new State(new Model(target.Resolution));

            var roundTripTrace = Command.GetBytes(trace).Select(i => (byte)i).ToList();

            Assert.IsTrue(traceBytes.SequenceEqual(roundTripTrace));

            state.Execute(trace);

            Assert.AreEqual(target, state.Model);
        }
    }

    [TestMethod]
    public void TestSolver()
    {
        var testCase = 20;

        var target = Model.Load(File.ReadAllBytes($"{root}\\problems\\LA{testCase:D3}_tgt.mdl"));

        var solution = Program.Solve(target);

        var state = new State(new Model(target.Resolution));

        state.Execute(solution);

        Assert.AreEqual(target, state.Model);
    }

    const string root = @"C:\Users\cashto\Documents\GitHub\icfp2018\";
}

