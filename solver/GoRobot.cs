// I called the teacher cause I wanted to confess it now
// Can I make the time for me to come and get it blessed somehow
// She spoke to me in such a simple and decisive tone
// Her sweet admission left me feeling in position from

using Newtonsoft.Json;
using Priority_Queue;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using System.Threading;
using System.Text;
using System.Threading.Tasks;

public class Program
{
    public static int MaxRobots = 40;

    public class Result
    {
        public string solution;
        public string model;
        public long score;
        public int solutionSize;
    }

    // I don't take these things so personal, anymore, anymore
    // I don't think it's irreversible, anymore
    static void Main(string[] args)
    {
        try
        {
            RealMain(
                args[0],
                int.Parse(args[1]),
                args.Length > 2 ? int.Parse(args[2]) : Program.MaxRobots);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    static void RealMain(string fileName, int timeout, int maxRobots)
    {
        Program.MaxRobots = maxRobots;

        var thread = new Thread(
            () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(timeout));
                Environment.Exit(-1);
            });

        thread.Start();

        var target = Model.Load(File.ReadAllBytes(fileName));

        var solution = Program.SolveParallel(target);

        var state = new State(new Model(target.Resolution));

        state.Execute(solution);

        var solutionFileName = @"d:\icfp2018\solutions\" + Guid.NewGuid().ToString("D");
        File.WriteAllBytes(solutionFileName, Command.GetBytes(solution).Select(i => (byte)i).ToArray());

        if (target.Equals(state.Model))
        {
            Console.Write(JsonConvert.SerializeObject(new Result()
            {
                score = -state.Energy,
                model = state.Model.ToString(),
                solutionSize = solution.Count(),
                solution = solutionFileName
            }));

            Console.Out.Flush();
        }

        Environment.Exit(0);
    }

    public class BotPlan
    {
        public Bot Bot { get; private set; }
        public BotPlan FusionTarget { get; private set; }
        public Chunk Chunk { get; private set; }

        private Action<Chunk> onPaintComplete;
        private IEnumerator<Command> paintMoves;
        private IEnumerator<Command> gotoMoves;
        private Command nextMove;
        private int fusionLock;
        private int failures;
        private int fissionThrottle;

        public BotPlan(Bot bot)
        {
            this.Bot = bot;
        }

        public bool IsIdle()
        {
            return 
                Chunk == null && 
                FusionTarget == null &&
                fusionLock == 0;
        }

        public void SetPaintJob(Chunk chunk, Action<Chunk> onPaintComplete)
        {
            this.Chunk = chunk;
            this.onPaintComplete = onPaintComplete;
        }

        public void SetSeekFusionJob(BotPlan other)
        {
            if (IsIdle())
            {
                Trace.Write($"{Bot.Id}: seek fusion with {other.Bot.Id} at {other.Bot.P}");
                ++other.fusionLock;
                this.FusionTarget = other;
            }
        }

        public void SetFusionJob()
        {
            if (FusionTarget.nextMove == null &&
                fusionLock == 0)
            {
                var delta = this.Bot.P - FusionTarget.Bot.P;
                this.nextMove = new Command(CommandType.FusionS) { P1 = -delta };
                FusionTarget.nextMove = new Command(CommandType.FusionP) { P1 = delta };
            }
        }

        public bool SetFissionJob(State state)
        {
            if (nextMove != null ||
                Bot.Seeds.Count == 0 ||
                fissionThrottle > 0)
            {
                return false;
            }

            var dir = Point.NearPoints.Where(d => state.CanMoveTo(this.Bot.P + d));
            if (!dir.Any())
            {
                return false;
            }

            this.nextMove = new Command(CommandType.Fission)
            {
                P1 = dir.First(),
                M = (Bot.Seeds.Count - 1) / 2
            };

            return true;
        }

        public Command GetCommand(State state, List<Chunk> workingChunks)
        {
            // Priorities: 
            // 1. fusion / fission (per nextMove)
            // 2. painting (but don't move if fusionLocked or someone in the way)
            // 3. wait if fusionLocked
            // 4. moving to paint chunk
            // 5. moving to fusion partner
            // 6. moving to origin

            --fissionThrottle;

            if (nextMove != null)
            {
                var command = nextMove;
                if (nextMove.Type == CommandType.FusionP)
                {
                    --fusionLock;
                    fissionThrottle = 5;
                }

                nextMove = null;
                return command;
            }
            else if (paintMoves != null)
            {
                return goPaint(state);
            }
            else if (Chunk != null && Bot.P.Equals(Chunk.GetCenter()))
            {
                Trace.Write($"{Bot.Id}: start painting {Chunk.GetCenter()}");

                // Trim SMoves off the end.
                paintMoves = Chunk.Paint(state, workingChunks)
                    .Reverse()
                    .SkipWhile(command => command.Type == CommandType.SMove)
                    .Reverse()
                    .GetEnumerator();
                
                paintMoves.MoveNext();
                return goPaint(state);
            }
            else if (fusionLock > 0)
            {
                return new Command(CommandType.Wait);
            }
            else
            {
                if (gotoMoves != null &&
                    gotoMoves.Current.CheckMove(state, Bot.P))
                {
                    return gotoMoves.Current;
                }

                var dest = 
                    Chunk != null ? Chunk.GetCenter() : 
                    FusionTarget != null ? FusionTarget.Bot.P :
                    Point.Zero;
                
                if (FusionTarget == null &&
                    state.Bots.Values.Any(b => b.P.Equals(dest)))
                {
                    Trace.Write($"{Bot.Id}: pathfinding failed because cell is occupied");
                    CheckDeadlock(false);
                    return new Command(CommandType.Wait);
                }

                // TODO: calculate paintMoves shortly upon arrival and include here 
                // so that peephole optimizer can eliminate stuttering.
                var path = state.PathFind(Bot.P, dest, FusionTarget != null);

                if (path == null || !path.Any())
                {
                    Trace.Write($"{Bot.Id}: pathfinding " + (path == null ? "failed" : "empty"));
                    CheckDeadlock(false);
                    return new Command(CommandType.Wait);
                }

                //return path.First();

                gotoMoves = path.GetEnumerator();
                gotoMoves.MoveNext();
                return gotoMoves.Current;
            }
        }

        private Command goPaint(State state)
        {
            var ret = paintMoves.Current;
            if (ret.Type == CommandType.SMove)
            {
                if (fusionLock > 0)
                {
                    Trace.Write($"{Bot.Id}: waiting for fusion to complete");
                    return new Command(CommandType.Wait);
                }
            }

            if (!state.CanMoveTo(Bot.P + ret.P1))
            {
                // someone else is in the way
                Trace.Write($"{Bot.Id}: failed to {ret}, someone in the way");
                this.CheckDeadlock(false);
                return new Command(CommandType.Wait);
            }

            return ret;
        }

        public void AcceptCommand(Command command)
        {
            if (command.Type != CommandType.Fill &&
                command.Type != CommandType.SMove &&
                command.Type != CommandType.LMove)
            {
                return;
            }

            if (paintMoves != null)
            {
                if (!paintMoves.MoveNext())
                {
                    Trace.Write($"{Bot.Id}: done painting {Chunk.GetCenter()}");
                    onPaintComplete(this.Chunk);
                    paintMoves = null;
                    this.Chunk = null;
                }
            }
            else if (gotoMoves != null)
            {
                if (!gotoMoves.MoveNext())
                {
                    gotoMoves = null;
                }
            }
        }

        public void CheckDeadlock(bool success)
        {
            failures = success ? 0 : failures + 1;
            if (failures >= 100)
            {
                throw new Exception("Bot is deadlocked");
            }
        }

        public bool Veto(
            State state,
            Bot other,
            Command command)
        {
            switch (command.Type)
            {
                //case CommandType.SMove:
                //case CommandType.LMove:
                //case CommandType.Fill:
                //case CommandType.Fission:
                //    break;
                default:
                    return false;
            }

            if (paintMoves != null)
            {
                return false;
            }

            var forbiddenPoint = other.P + command.P1 + command.P2;

            var dest =
                Chunk != null ? Chunk.GetCenter() :
                FusionTarget != null ? FusionTarget.Bot.P :
                Point.Zero;

            bool veto = state.PathFind(Bot.P, dest, FusionTarget != null, forbiddenPoint) == null;

            if (veto)
            {
                Trace.Write($"{other.Id}: {command} vetoed by {Bot.Id}");
            }

            return veto;
        }
    }

    // Tell me now, I know that it just won't stop
    // You will find your flow when you GO ROBOT
    // I want to thank you and spank you upon your silver skin
    // Robots don't care where I've been
    // You've got to choose it to use it, so let me plug it in
    // Robots are my next of kin
    public static IEnumerable<Command> SolveParallel(Model target)
    {
        var state = new State(new Model(target.Resolution));
        var botPlans = new Dictionary<int, BotPlan>();
        var theBot = state.Bots.First().Value;
        botPlans[theBot.Id] = new BotPlan(theBot);

        var doneChunks = new List<Chunk>();
        var workingChunks = new List<Chunk>();
        var allChunks = target.GetChunks()
            .Where(chunk => chunk.TargetPointsCount > 0)
            .ToList();
        var readyChunks = allChunks
            .Where(chunk => chunk.Offset.Y == 0 && chunk.IsFillable(state.Model))
            .ToList();
        var newlyDoneChunks = new List<Chunk>();
        bool allowFission = true;

        while (
            doneChunks.Count != allChunks.Count ||
            state.Bots.Count != 1 ||
            !state.Bots.First().Value.P.Equals(Point.Zero))
        {
            Trace.Write($"Progress: {100.0 * doneChunks.Count / allChunks.Count:F2}%");

            // Update readyChunks
            foreach (var chunk in newlyDoneChunks)
            {
                doneChunks.Add(chunk);
                var newChunks = allChunks
                    .Where(c => (c.Offset - chunk.Offset).MDist() == 3)
                    .Where(c => !readyChunks.Contains(c))
                    .Where(c => !workingChunks.Contains(c))
                    .Where(c => c.IsFillable(state.Model));
                readyChunks.AddRange(newChunks);
            }

            newlyDoneChunks.Clear();

            // don't start work next to another chunk in progress
            var reallyReadyChunks = readyChunks.Where(chunk =>
                Point.CardinalDirections.All(dir =>
                    botPlans.Values.All(b => b.Chunk == null || !b.Chunk.Offset.Equals(chunk.Offset + 3 * dir))))
                .ToList();

            // Fission new bots
            if (allowFission)
            {
                int newBots = Math.Min(Program.MaxRobots - botPlans.Count, reallyReadyChunks.Count - botPlans.Count);
                for (var i = 0; i < newBots; ++i)
                {
                    var chunk = reallyReadyChunks[i];

                    var availableBots =
                        from bot in botPlans.Values
                        orderby (bot.Bot.P - chunk.GetCenter()).MDist()
                        select bot;

                    foreach (var bot in availableBots)
                    {
                        if (bot.SetFissionJob(state))
                        {
                            break;
                        }
                    }
                }
            }

            // Assign chunks to idle bots
            do
            {
                var idleBots = botPlans.Values.Where(bot => bot.IsIdle());
                if (!idleBots.Any())
                {
                    break;
                }

                var possibleJobs =
                    from chunk in reallyReadyChunks
                    // make sure we're not accidentally sealing a chunk in
                    //let newWorkingChunks = workingChunks.Concat(new List<Chunk>() { chunk })
                    //where readyChunks.All(c =>
                    //    Point.CardinalDirections.Any(dir =>
                    //        !newWorkingChunks.Any(w => w.Offset.Equals(c.Offset + 3 * dir))))
                    let isReallyReallyReadyChunk = state.Model.CheckSolid(target, workingChunks, chunk) == 0
                    where isReallyReallyReadyChunk
                    from bot in idleBots
                    let mdist = (chunk.GetCenter() - bot.Bot.P).MDist()
                    orderby mdist
                    select new { bot = bot, chunk = chunk };

                if (!possibleJobs.Any())
                {
                    if (state.Bots.Count == 1 &&
                        reallyReadyChunks.Count != 0)
                    {
                        possibleJobs =
                            from chunk in reallyReadyChunks
                            from bot in idleBots
                            orderby state.Model.CheckSolid(target, workingChunks, chunk)
                            select new { bot = bot, chunk = chunk };

                        Trace.Write("EMERGENCY");
                        allowFission = false;
                    }
                    else
                    {
                        break;
                    }
                }

                var firstJob = possibleJobs.First();
                firstJob.bot.SetPaintJob(firstJob.chunk, (chunk) =>
                {
                    newlyDoneChunks.Add(chunk);
                });

                reallyReadyChunks = readyChunks.Where(chunk =>
                    Point.CardinalDirections.All(dir =>
                        botPlans.Values.All(b => b.Chunk == null || !b.Chunk.Offset.Equals(chunk.Offset + 3 * dir))))
                    .ToList();

                readyChunks.Remove(firstJob.chunk);

                Trace.Write($"{firstJob.bot.Bot.Id}: adding chunk {firstJob.chunk}");
                workingChunks.Add(firstJob.chunk);
            } while (true);

            // Seek to fuse idle bots
            foreach (var idleBot in botPlans.Values.Where(bot => bot.IsIdle()))
            {
                if (state.Bots.Count > 1)
                {
                    var closestBot = botPlans.Values
                        .Where(bot => bot != idleBot)
                        .OrderBy(bot => (idleBot.Bot.P - bot.Bot.P).MDist())
                        .First();

                    idleBot.SetSeekFusionJob(closestBot);
                }
            }

            // Fuse bots
            foreach (var bot in botPlans.Values
                .Where(b => b.FusionTarget != null)
                .Where(b => (b.Bot.P - b.FusionTarget.Bot.P).IsNear()))
            {
                bot.SetFusionJob();
            }

            // Execute commands
            var volatileCells = new VolatileCells();
            var commands = new List<Command>();
            foreach (var bot in botPlans.Values.OrderBy(b => b.Bot.Id))
            {
                var command = bot.GetCommand(state, workingChunks);
                var newVolatileCells = command.GetVolatileCells(bot.Bot.P);
                var success = 
                    botPlans.Values.All(b => b == bot || !b.Veto(state, bot.Bot, command)) &&
                    volatileCells.Add(newVolatileCells);

                if (success)
                {
                    bot.AcceptCommand(command);
                }

                bot.CheckDeadlock(success);

                if (!success)
                {
                    Trace.Write($"{bot.Bot.Id}: failed to {command}");
                    command = new Command(CommandType.Wait);
                    newVolatileCells = command.GetVolatileCells(bot.Bot.P);
                    success = volatileCells.Add(newVolatileCells);
                    if (!success)
                    {
                        throw new Exception("Can't even wait successfully");
                    }
                }

                // Trace.Write($"{bot.Bot.Id}: at {bot.Bot.P}, {command}");
                yield return command;
                commands.Add(command);
            }

            state.Execute(commands, paint: true);

            // Update BotPlans due to bots being created or destroyed
            botPlans = state.Bots.Values.ToDictionary(
                bot => bot.Id,
                bot => botPlans.ContainsKey(bot.Id) ? botPlans[bot.Id] : new BotPlan(bot));
        }

        yield return new Command(CommandType.Halt);
    }
}

public class State
{
    public Model Model;
    public int Energy;
    public bool HighHarmonics;
    public SortedList<int, Bot> Bots = new SortedList<int, Bot>();

    public State(Model model)
    {
        Model = model;
        Bots.Add(1, new Bot()
        {
            Id = 1,
            P = Point.Zero,
            Seeds = Enumerable.Range(2, 39).ToList()
        });
    }

    public void Execute(
        IEnumerable<Command> commands,
        bool paint = false)
    {
        var commandsEnum = commands.GetEnumerator();

        while (Bots.Any())
        {
            if (!paint && !isWellFormed())
            {
                throw new Exception("State is not well formed");
            }

            var r = Model.Resolution;
            Energy += (HighHarmonics ? 30 : 3) * r * r * r + 20 * Bots.Count;

            var commandsToExecute = new List<Tuple<Command, Bot>>();
            foreach (var bot in Bots.Values)
            {
                if (!commandsEnum.MoveNext())
                {
                    if (commandsToExecute.Any())
                    {
                        throw new Exception("Not enough commands");
                    }

                    goto breakout;
                }

                commandsToExecute.Add(Tuple.Create(commandsEnum.Current, bot));
            }

            foreach (var command in commandsToExecute)
            {
                command.Item1.CheckFusions(command.Item2, commandsToExecute);
            }

            var volatileCells = new VolatileCells();
            foreach (var command in commandsToExecute)
            {
                command.Item1.Execute(this, command.Item2, volatileCells);
            }
        }

    breakout:
        if (Bots.Any())
        {
            return;
        }

        if (commandsEnum.MoveNext())
        {
            throw new Exception("Unexecuted commands after halt");
        }

        if (!isWellFormedTerminalState())
        {
            throw new Exception("Terminal state is not well formed");
        }
    }

    public State Clone()
    {
        return new State(Model.Clone())
        {
            HighHarmonics = HighHarmonics,
            Energy = Energy,
            Bots = Bots.Clone()
        };
    }

    public bool CanMoveTo(Point p)
    {
        return
            Model.InBounds(p) &&
            !Model.Get(p) &&
            !Bots.Values.Any(bot => bot.P.Equals(p));
    }

    class PriorityQueueNode
    {
        public PriorityQueueNode Previous;
        public Point P;
        public Point Dir;

        public PriorityQueueNode(
            PriorityQueueNode previous,
            Point p,
            Point dir)
        {
            Previous = previous;
            P = p;
            Dir = dir;
        }
    }

    public IEnumerable<Command> PathFind(
        Point src,
        Point dest,
        bool justGetNear,
        Point? forbiddenPoint = null)
    {
        var ans = pathFindImpl(src, dest, Point.Zero, justGetNear, forbiddenPoint);
        if (ans == null)
        {
            return null;
        }

        var peepholeOptimizers = new List<Func<IEnumerable<Command>, IEnumerable<Command>>>()
        {
            filterNullSMoves,
            combineSMoves,
            splitSMoves,
            packSMoves
        };

        foreach (var optimizer in peepholeOptimizers)
        {
            ans = optimizer(ans);
        }

        return ans;
    }

    private IEnumerable<Command> pathFindImpl(
        Point src,
        Point dest,
        Point forwards,
        bool justGetNear,
        Point? forbiddenPoint)
    {
        var costs = new Dictionary<Tuple<Point, Point>, int>();
        costs[Tuple.Create(src, forwards)] = 0;

        var pq = new SimplePriorityQueue<PriorityQueueNode, int>();
        pq.Enqueue(
            new PriorityQueueNode(null, src, forwards),
            (dest - src).MDist());

        while (pq.Count > 0)
        {
            var node = pq.Dequeue();
            if (justGetNear ? (dest - node.P).IsNear() : node.P.Equals(dest))
            {
                return getMoves(node).Reverse().Skip(1);
            }

            foreach (var dir in Point.CardinalDirections)
            {
                var p = node.P + dir;
                if (this.CanMoveTo(p) && !forbiddenPoint.Equals(p))
                {
                    var newKey = Tuple.Create(p, dir);
                    if (!costs.ContainsKey(newKey))
                    {
                        var previousCost = costs[Tuple.Create(node.P, node.Dir)];
                        var newCost = previousCost + (dir.Equals(node.Dir) || dir.Equals(Point.Zero) ? 1 : 10);
                        costs[newKey] = newCost;
                        pq.Enqueue(new PriorityQueueNode(node, p, dir), newCost + (dest - p).MDist());
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<Command> getMoves(PriorityQueueNode node)
    {
        while (node != null)
        {
            yield return new Command(CommandType.SMove) { P1 = node.Dir };
            node = node.Previous;
        }
    }

    private static IEnumerable<Command> filterNullSMoves(IEnumerable<Command> commands)
    {
        return
            from command in commands
            where command.Type != CommandType.SMove || command.P1.MDist() > 0
            select command;
    }

    private static IEnumerable<Command> splitSMoves(IEnumerable<Command> commands)
    {
        foreach (var command in commands)
        {
            if (command.Type != CommandType.SMove)
            {
                yield return command;
            }
            else
            {
                var dist = command.P1.MDist();
                var axis = command.P1.GetAxis();
                var neg = command.P1.GetIndex(axis) < 0 ? -1 : 1;
                var segments = (dist + 14) / 15;

                foreach (var i in Enumerable.Range(0, segments))
                {
                    var length = (i == 0) ? ((dist - 1) % 15 + 1) : 15;
                    yield return new Command(CommandType.SMove) { P1 = Point.FromAxisIndex(axis, length * neg) };
                }
            }
        }
    }

    private static IEnumerable<Command> packSMoves(IEnumerable<Command> commands)
    {
        Command lastCommand = null;

        foreach (var c in commands)
        {
            var command = c;

            if (lastCommand != null &&
                command != null &&
                lastCommand.Type == CommandType.SMove &&
                command.Type == CommandType.SMove &&
                lastCommand.P1.GetAxis() != command.P1.GetAxis() &&
                lastCommand.P1.IsShortLinear() &&
                command.P1.IsShortLinear())
            {
                command = new Command(CommandType.LMove) { P1 = lastCommand.P1, P2 = command.P1 };
            }
            else if (lastCommand != null)
            {
                yield return lastCommand;
            }

            lastCommand = command;
        }

        if (lastCommand != null)
        {
            yield return lastCommand;
        }
    }

    private static IEnumerable<Command> combineSMoves(IEnumerable<Command> commands)
    {
        Command lastCommand = null;

        foreach (var c in commands)
        {
            var command = c;

            if (lastCommand != null &&
                command != null &&
                lastCommand.Type == CommandType.SMove &&
                command.Type == CommandType.SMove &&
                lastCommand.P1.GetAxis() == command.P1.GetAxis())
            {
                command = new Command(CommandType.SMove) { P1 = lastCommand.P1 + command.P1 };
                if (command.P1.MDist() == 0)
                {
                    command = null;
                }
            }
            else if (lastCommand != null)
            {
                yield return lastCommand;
            }

            lastCommand = command;
        }

        if (lastCommand != null)
        {
            yield return lastCommand;
        }
    }

    public bool isWellFormed()
    {
        return
            (HighHarmonics || Model.IsGrounded()) &&
            Bots.Values.All(bot => !Model.Get(bot.P));
    }

    public bool isWellFormedTerminalState()
    {
        return
            isWellFormed() &&
            !HighHarmonics &&
            !Bots.Any();
    }
}

public class Model
{
    public int Resolution { get; private set; }
    public bool? IsGroundedCache { get; protected set; }
    private bool[] matrix;

    protected Model()
    {
    }

    public Model(int resolution)
    {
        Resolution = resolution;
        matrix = new bool[resolution * resolution * resolution];
    }

    public override bool Equals(object obj)
    {
        var other = obj as Model;

        return
            other != null &&
            Resolution == other.Resolution &&
            matrix.SequenceEqual(other.matrix);
    }

    public List<string> Compare(Model expected)
    {
        var ret = new List<string>();
        for (var x = 0; x < Resolution; ++x)
        {
            for (var y = 0; y < Resolution; ++y)
            {
                for (var z = 0; z < Resolution; ++z)
                {
                    var p = new Point(x, y, z);
                    if (Get(p) != expected.Get(p))
                    {
                        ret.Add($"{p} should be {expected.Get(p)}");
                    }
                }
            }
        }

        return ret;
    }

    public override int GetHashCode()
    {
        return 42;
    }

    public override string ToString()
    {
        return $"Model {Resolution}x{Resolution}x{Resolution}, {matrix.Count(i => i)} filled";
    }

    public Model Clone()
    {
        return new Model()
        {
            Resolution = Resolution,
            matrix = matrix.ToArray()
        };
    }

    public void Clear()
    {
        Array.Clear(matrix, 0, matrix.Length);
    }

    virtual public void Set(Point p)
    {
        updateGrounded(p);
        matrix[p.X * Resolution * Resolution + p.Y * Resolution + p.Z] = true;
    }

    public IEnumerable<Chunk> GetChunks()
    {
        for (var x = 1; x < Resolution - 1; x += 3)
        {
            for (var y = 0; y < Resolution - 1; y += 3)
            {
                for (var z = 1; z < Resolution - 1; z += 3)
                {
                    yield return new Chunk(this, new Point(x, y, z));
                }
            }
        }
    }

    public int CheckSolid(
        Model target,
        List<Chunk> workingChunks,
        Chunk newChunk)
    {
        var start = DateTime.UtcNow;

        var pointsSet = 0;
        var r = (Resolution - 1) / 3 + 2;
        var model = new Model(r);

        foreach (var chunk in workingChunks.Concat(new List<Chunk>() { newChunk }))
        {
            var p = new Point(
                (chunk.Offset.X - 1) / 3 + 1,
                (chunk.Offset.Y - 0) / 3,
                (chunk.Offset.Z - 1) / 3 + 1);
            model.Set(p);
            ++pointsSet;
        }

        var points = new HashSet<Point>();
        points.Add(Point.Zero);

        while (points.Any())
        {
            var p = points.First();
            points.Remove(p);

            foreach (var dir in Point.CardinalDirections)
            {
                if (model.InBounds(p + dir) && 
                    !model.Get(p + dir) &&
                    !points.Contains(p + dir))
                {
                    points.Add(p + dir);
                }
            }

            ++pointsSet;
            model.Set(p);
        }

        var ret = r * r * r - pointsSet;
        //Trace.Write($"CheckSolid returns {ret} in {(DateTime.UtcNow - start).TotalMilliseconds} ms");
        return ret;
    }

    protected void updateGrounded(Point p)
    {
        if (IsGroundedCache.Equals(true) &&
            p.Y > 0 &&
            !Point.CardinalDirections.Any(adj => Get(p + adj)))
        {
            // If we were grounded, but the new point has no neighbors and is not on the ground -- 
            // we're not grounded any more.
            IsGroundedCache = false;
        }
        else if (
            IsGroundedCache.Equals(false) &&
            Point.CardinalDirections.Count(adj => Get(p + adj)) >= 2)
        {
            // If we were not grounded, we might become grounded if we have two or more neighbors.
            IsGroundedCache = null;
        }
    }

    public bool IsGrounded()
    {
        if (!IsGroundedCache.HasValue)
        {
            IsGroundedCache = computeGrounded();
        }

        return IsGroundedCache.Value;
    }

    virtual public bool Get(Point p)
    {
        if (!InBounds(p))
        {
            throw new Exception($"Point {p} is out of bounds");
        }

        return matrix[p.X * Resolution * Resolution + p.Y * Resolution + p.Z];
    }

    public static Model Load(IEnumerable<byte> ss)
    {
        var s = ss.GetEnumerator();
        var resolution = s.ReadByte();
        var ret = new Model(resolution);
        var bits = getBits(s).GetEnumerator();

        for (var x = 0; x < resolution; ++x)
        {
            for (var y = 0; y < resolution; ++y)
            {
                for (var z = 0; z < resolution; ++z)
                {
                    bits.MoveNext();
                    if (bits.Current)
                    {
                        ret.Set(new Point(x, y, z));
                    }
                }
            }
        }
        return ret;
    }

    public bool InBounds(Point p)
    {
        return
            p.X >= 0 &&
            p.Y >= 0 &&
            p.Z >= 0 &&
            p.X < Resolution &&
            p.Y < Resolution &&
            p.Z < Resolution;
    }

    private static IEnumerable<bool> getBits(IEnumerator<byte> s)
    {
        while (true)
        {
            int b = s.ReadByte();
            for (var i = 0; i < 8; ++i)
            {
                yield return (b & 1) != 0;
                b = b >> 1;
            }
        }
    }

    private bool computeGrounded()
    {
        var groundedPoints = new Model(Resolution);
        var borderPoints = new Queue<Point>();
        var filledCellsCount = 0;
        var groundedCellsCount = 0;

        for (var y = 0; y < Resolution; y++)
        {
            for (var x = 0; x < Resolution; x++)
            {
                for (var z = 0; z < Resolution; z++)
                {
                    var p = new Point(x, y, z);
                    if (Get(p))
                    {
                        ++filledCellsCount;

                        if (y == 0)
                        {
                            ++groundedCellsCount;
                            groundedPoints.Set(p);
                            borderPoints.Enqueue(p);
                        }
                    }
                }
            }
        }

        while (borderPoints.Any())
        {
            var p = borderPoints.Dequeue();

            foreach (var adjacency in Point.CardinalDirections)
            {
                var p2 = p + adjacency;
                if (InBounds(p2) &&
                    Get(p2) && 
                    !groundedPoints.Get(p2))
                {
                    ++groundedCellsCount;
                    groundedPoints.Set(p2);
                    borderPoints.Enqueue(p2);
                }
            }
        }

        return filledCellsCount == groundedCellsCount;
    }
}

public class Bot
{
    public int Id;
    public Point P;
    public List<int> Seeds;

    public Bot Clone()
    {
        return new Bot()
        {
            Id = Id,
            P = P,
            Seeds = Seeds.ToList()
        };
    }
}

public struct Point
{
    public static readonly Point Zero = new Point(0, 0, 0);

    public int X { get; private set; }
    public int Y { get; private set; }
    public int Z { get; private set; }

    public static readonly Point[] CardinalDirections = new Point[]
    {
        new Point(1, 0, 0),
        new Point(-1, 0, 0),
        new Point(0, 0, 1),
        new Point(0, 0, -1),
        new Point(0, 1, 0),
        new Point(0, -1, 0)
    };

    public static readonly Point[] NearPoints = generateNearPoints().ToArray();

    public Point(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static Point operator +(Point lhs, Point rhs)
    {
        return new Point(
            lhs.X + rhs.X,
            lhs.Y + rhs.Y,
            lhs.Z + rhs.Z);
    }

    public static Point operator -(Point lhs, Point rhs)
    {
        return new Point(
            lhs.X - rhs.X,
            lhs.Y - rhs.Y,
            lhs.Z - rhs.Z);
    }

    public static Point operator -(Point p)
    {
        return -1 * p;
    }

    public static Point operator *(int scale, Point p)
    {
        return new Point(
            scale * p.X,
            scale * p.Y,
            scale * p.Z);
    }


    public override string ToString()
    {
        return $"<{X},{Y},{Z}>";
    }

    public static Point FromAxisIndex(int axis, int index)
    {
        switch (axis)
        {
            case 1: return new Point(index, 0, 0);
            case 2: return new Point(0, index, 0);
            case 3: return new Point(0, 0, index);
            default: throw new Exception($"Axis {axis} is invalid");
        }
    }

    public static Point FromNearDistance(int nd)
    {
        var ret = new Point(
            nd / 9 - 1,
            (nd / 3) % 3 - 1,
            nd % 3 - 1);

        if (!ret.IsNear())
        {
            throw new Exception($"Distance {nd} => {ret} is not near");
        }

        return ret;
    }

    public int ToNearDistance()
    {
        if (!IsNear())
        {
            throw new Exception("Point {this} is not near");
        }

        return (X + 1) * 9 + (Y + 1) * 3 + (Z + 1);
    }

    public int GetAxis()
    {
        if (!IsLinear())
        {
            throw new Exception($"Cannot get axis of non-linear point {this}");
        }

        return
            X != 0 ? 1 :
            Y != 0 ? 2 :
            3;
    }

    public int GetIndex(int axis)
    {
        if (axis < 1 || axis > 3)
        {
            throw new Exception($"Axis {axis} is invalid");
        }

        return
            axis == 1 ? X :
            axis == 2 ? Y :
            Z;
    }

    public int MDist()
    {
        return Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z);
    }

    public int CDist()
    {
        return Math.Max(Math.Max(Math.Abs(X), Math.Abs(Y)), Math.Abs(Z));
    }

    public bool IsLinear()
    {
        return MDist() > 0 && MDist() == CDist();
    }

    public bool IsShortLinear()
    {
        return IsLinear() && MDist() <= 5;
    }

    public bool IsLongLinear()
    {
        return IsLinear() && MDist() <= 15;
    }

    public bool IsNear()
    {
        return
            0 < MDist() &&
            MDist() <= 2 &&
            CDist() == 1;
    }

    private static IEnumerable<Point> generateNearPoints()
    {
        for (var x = 0; x < 3; ++x)
        {
            for (var y = 0; y < 3; ++y)
            {
                for (var z = 0; z < 3; ++z)
                {
                    var p = new Point(x - 1, y - 1, z - 1);
                    if (p.IsNear())
                    {
                        yield return p;
                    }
                }
            }
        }
    }
}

public enum CommandType
{
    Halt,
    Wait,
    Flip,
    SMove,
    LMove,
    FusionP,
    FusionS,
    Fission,
    Fill
}

public class Command
{
    public CommandType Type;
    public Point P1;
    public Point P2;
    public int M;

    public Command(CommandType type)
    {
        Type = type;
    }

    public override string ToString()
    {
        return $"{Type} ({P1}, {P2}, {M})";
    }

    public static IEnumerable<Command> GetCommands(IEnumerable<byte> ss)
    {
        var s = ss.GetEnumerator();
        while (s.MoveNext())
        {
            int b = s.Current;
            if (b == 0xFF)
            {
                yield return new Command(CommandType.Halt);
            }
            else if (b == 0xFE)
            {
                yield return new Command(CommandType.Wait);
            }
            else if (b == 0xFD)
            {
                yield return new Command(CommandType.Flip);
            }
            else if ((b & 0xCF) == 0x04)
            {
                var axis = (b & 0x30) >> 4;
                var index = s.ReadByte() - 15;
                yield return new Command(CommandType.SMove)
                {
                    P1 = Point.FromAxisIndex(axis, index)
                };
            }
            else if ((b & 0x0F) == 0x0C)
            {
                var axis1 = (b & 0x30) >> 4;
                var axis2 = (b & 0xC0) >> 6;
                var index1 = (b & 0x0F);
                var index2 = (b & 0xF0) >> 4;
                yield return new Command(CommandType.LMove)
                {
                    P1 = Point.FromAxisIndex(axis1, index1),
                    P2 = Point.FromAxisIndex(axis2, index2)
                };
            }
            else if ((b & 0x06) == 0x06)
            {
                var commandType = ((b & 0x01) != 0) ? CommandType.FusionP : CommandType.FusionS;
                var nd = (b & 0xF8) >> 3;
                yield return new Command(commandType)
                {
                    P1 = Point.FromNearDistance(nd)
                };
            }
            else if ((b & 0x07) == 0x05)
            {
                var nd = (b & 0xF8) >> 3;
                var m = s.ReadByte();
                yield return new Command(CommandType.Fission)
                {
                    P1 = Point.FromNearDistance(nd),
                    M = m
                };
            }
            else if ((b & 0x07) == 0x03)
            {
                var nd = (b & 0xF8) >> 3;
                yield return new Command(CommandType.Fill)
                {
                    P1 = Point.FromNearDistance(nd)
                };
            }
        }
    }

    public static IEnumerable<int> GetBytes(IEnumerable<Command> commands)
    {
        foreach (var command in commands)
        {
            switch (command.Type)
            {
                case CommandType.Halt:
                    yield return 0xFF;
                    break;
                case CommandType.Wait:
                    yield return 0xFE;
                    break;
                case CommandType.Flip:
                    yield return 0xFD;
                    break;
                case CommandType.SMove:
                    var axis = command.P1.GetAxis();
                    yield return (axis << 4) + 0x04;
                    yield return command.P1.GetIndex(axis) + 15;
                    break;
                case CommandType.LMove:
                    var axis1 = command.P1.GetAxis();
                    var axis2 = command.P2.GetAxis();
                    yield return (axis2 << 6) + (axis1 << 4) + 0x0C;
                    yield return ((command.P2.GetIndex(axis2) + 5) << 4) + (command.P1.GetIndex(axis1) + 5);
                    break;
                case CommandType.FusionP:
                    yield return (command.P1.ToNearDistance() << 3) + 0x07;
                    break;
                case CommandType.FusionS:
                    yield return (command.P1.ToNearDistance() << 3) + 0x06;
                    break;
                case CommandType.Fission:
                    yield return (command.P1.ToNearDistance() << 3) + 0x05;
                    yield return command.M;
                    break;
                case CommandType.Fill:
                    yield return (command.P1.ToNearDistance() << 3) + 0x03;
                    break;
                default: throw new Exception($"Cannot serialize command type {command.Type}");
            }
        }
    }

    public void CheckFusions(Bot bot, IEnumerable<Tuple<Command, Bot>> otherCommands)
    {
        if (Type == CommandType.FusionS)
        {
            // For every FusionS, make sure it points to one (and only one) FusionP.
            var primaryBot = otherCommands.Single(other =>
                other.Item1.Type == CommandType.FusionP &&
                other.Item2.P.Equals(bot.P + P1));
        }
        else if (Type == CommandType.FusionP)
        {
            // For every FusionP, make sure it points to one (and only one) FusionS.
            var secondaryBot = otherCommands.Single(other =>
                other.Item1.Type == CommandType.FusionS &&
                other.Item2.P.Equals(bot.P + P1));
        }
    }

    public VolatileCells GetVolatileCells(
        Point botPosition)
    {
        var ret = new VolatileCells();
        ret.Add(botPosition);

        switch (Type)
        {
            case CommandType.SMove:
                checkMove(null, ret, botPosition, P1);
                break;

            case CommandType.LMove:
                checkMove(null, ret, botPosition, P1);
                checkMove(null, ret, botPosition + P1, P2);
                break;

            case CommandType.Fission:
                ret.Add(botPosition + P1);
                break;

            case CommandType.Fill:
                ret.Add(botPosition + P1);
                break;
        }

        return ret;
    }

    public void Execute(
        State state,
        Bot bot,
        VolatileCells volatileCells)
    {
        volatileCells.Check(bot.P);

        switch (Type)
        {
            case CommandType.Halt:
                if (!bot.P.Equals(Point.Zero))
                {
                    throw new Exception("Halt not executed at <0,0,0>");
                }

                if (state.Bots.Count != 1)
                {
                    throw new Exception($"Halt executed with {state.Bots.Count} bots remaining");
                }

                state.Bots.Clear();
                break;

            case CommandType.Wait:
                break;

            case CommandType.Flip:
                state.HighHarmonics = !state.HighHarmonics;
                break;

            case CommandType.SMove:
                if (!P1.IsLongLinear())
                {
                    throw new Exception($"Cannot do SMove: {P1} is not long linear");
                }

                checkMove(state.Model, volatileCells, bot.P, P1);
                bot.P = bot.P + P1;

                state.Energy += 2 * P1.MDist();
                break;

            case CommandType.LMove:
                if (!P1.IsShortLinear())
                {
                    throw new Exception($"Cannot do LMove: {P1} is not short linear");
                }

                if (!P2.IsShortLinear())
                {
                    throw new Exception($"Cannot do LMove: {P2} is not short linear");
                }

                checkMove(state.Model, volatileCells, bot.P, P1);
                bot.P = bot.P + P1;

                checkMove(state.Model, volatileCells, bot.P, P2);
                bot.P = bot.P + P2;

                state.Energy += 2 * P1.MDist() + 2 * P2.MDist() + 4;
                break;

            case CommandType.FusionP:
                if (!P1.IsNear())
                {
                    throw new Exception($"Cannot do FusionP: {P1} is not near");
                }

                var secondaryBot = state.Bots.Values.Single(other => other.P.Equals(bot.P + P1));
                state.Bots.Remove(secondaryBot.Id);
                bot.Seeds.Add(secondaryBot.Id);
                state.Energy -= 24;
                break;

            case CommandType.FusionS:
                break;

            case CommandType.Fission:
                if (!P1.IsNear())
                {
                    throw new Exception($"Cannot fission: {P1} is not near");
                }

                var p2 = bot.P + P1;
                volatileCells.Check(p2);

                if (state.Model.Get(p2))
                {
                    throw new Exception($"Cannot fission bot {bot.Id}: point {p2} is occupied");
                }

                if (bot.Seeds.Count < M + 1)
                {
                    throw new Exception($"Cannot fission bot {bot.Id}: bot has {bot.Seeds.Count} seeds and {M + 1} are needed");
                }

                bot.Seeds.Sort();

                var newBot = new Bot()
                {
                    Id = bot.Seeds[0],
                    P = p2,
                    Seeds = bot.Seeds.Skip(1).Take(M).ToList()
                };

                bot.Seeds = bot.Seeds.Skip(M + 1).ToList();
                state.Bots.Add(newBot.Id, newBot);
                state.Energy += 24;
                break;

            case CommandType.Fill:
                if (!P1.IsNear())
                {
                    throw new Exception($"Cannot fill: {P1} is not near");
                }

                p2 = bot.P + P1;
                volatileCells.Check(p2);

                if (p2.X <= 0 ||
                    p2.Y < 0 ||
                    p2.Z <= 0 ||
                    p2.X >= state.Model.Resolution - 1 ||
                    p2.Y >= state.Model.Resolution - 1 ||
                    p2.Z >= state.Model.Resolution - 1)
                {
                    throw new Exception($"Cannot fill: {p2} is out of bounds");
                }

                state.Energy += state.Model.Get(p2) ? 6 : 12;
                state.Model.Set(p2);
                break;

            default:
                throw new Exception($"Cannot execute command type {Type}");
        }
    }

    public bool CheckMove(State state, Point p)
    {
        switch (Type)
        {
            case CommandType.LMove:
                return 
                    checkMoveImpl(state, p, P1) &&
                    checkMoveImpl(state, p + P1, P2);
            case CommandType.SMove:
                return checkMoveImpl(state, p, P1);
            default:
                throw new Exception("Cannot check move of type {Type}");
        }
    }

    private bool checkMoveImpl(
        State state,
        Point p,
        Point d)
    {
        var axis = d.GetAxis();
        var index = d.GetIndex(axis);
        var neg = index > 0 ? 1 : -1;

        foreach (var i in Enumerable.Range(1, Math.Abs(index)))
        {
            var p2 = p + Point.FromAxisIndex(axis, i * neg);
            if (state.Model.Get(p2) ||
                state.Bots.Values.Any(bot => bot.P.Equals(p2)))
            {
                return false;
            }
        }

        return true;
    }

    private bool checkMove(
        Model model,
        VolatileCells volatileCells,
        Point p,
        Point d)
    {
        var axis = d.GetAxis();
        var index = d.GetIndex(axis);
        var neg = index > 0 ? 1 : -1;

        foreach (var i in Enumerable.Range(1, Math.Abs(index)))
        {
            var p2 = p + Point.FromAxisIndex(axis, i * neg);
            if (model != null && model.Get(p2))
            {
                throw new Exception($"Cannot move: {p} to {d}: {p2}");
            }

            volatileCells.Check(p2);
        }

        return true;
    }
}

public class VolatileCells
{
    private HashSet<Point> cells = new HashSet<Point>();

    public void Check(Point p)
    {
        if (!Add(p))
        {
            throw new Exception($"Point {p} is already a volatile cell");
        }
    }

    public bool Add(Point p)
    {
        return cells.Add(p);
    }

    public bool Add(VolatileCells other)
    {
        if (other.cells.Any(cell => this.cells.Contains(cell)))
        {
            return false;
        }

        foreach (var cell in other.cells)
        {
            Add(cell);
        }

        return true;
    }
}

public static class ExtensionMethods
{
    public static byte ReadByte(this IEnumerator<byte> s)
    {
        s.MoveNext();
        return s.Current;
    }

    public static int TotalLength(this IEnumerable<Command> commands)
    {
        return commands
            .Where(command => command.Type == CommandType.SMove)
            .Sum(command => command.P1.MDist());
    }

    public static SortedList<int, Bot> Clone(this SortedList<int, Bot> bots)
    {
        var ret = new SortedList<int, Bot>();
        foreach (var bot in bots.Values.Select(b => b.Clone()))
        {
            ret.Add(bot.Id, bot);
        }

        return ret;
    }
}

public class Chunk
{
    public Point Offset;
    public int TargetPointsCount { get; private set; }

    private Model target;
    private Model overrideModel;

    public static Chunk Origin = new Chunk(null, new Point(-1, -1, -1));
    
    public Chunk(Model target, Point offset)
    {
        this.target = target;
        Offset = offset;
        overrideModel = new Model(3);

        if (target != null)
        {
            for (var x = 0; x < 3; ++x)
            {
                for (var y = 0; y < 3; ++y)
                {
                    for (var z = 0; z < 3; ++z)
                    {
                        var p = Offset + new Point(x, y, z);
                        if (target.InBounds(p) && target.Get(p))
                        {
                            ++TargetPointsCount;
                        }
                    }
                }
            }
        }
    }

    public override string ToString()
    {
        return GetCenter().ToString();
    }

    public Point GetCenter()
    {
        return Offset + new Point(1, 1, 1);
    }

    private bool get(Model model, Point p)
    {
        return isInRange(p) ? overrideModel.Get(p - Offset) : model.Get(p);
    }

    private void set(Point p)
    {
        if (!isInRange(p))
        {
            throw new Exception($"Point {p} is not in range");
        }

        overrideModel.Set(p - Offset);
    }

    private bool isInRange(Point p)
    {
        return
            p.X >= Offset.X &&
            p.Y >= Offset.Y &&
            p.Z >= Offset.Z &&
            p.X < Offset.X + overrideModel.Resolution &&
            p.Y < Offset.Y + overrideModel.Resolution &&
            p.Z < Offset.Z + overrideModel.Resolution;
    }

    public List<Command> PaintAndMoveTo(
        State state, 
        Chunk newChunk)
    {
        IEnumerable<IEnumerable<Command>> plans =
            (from direction in Point.CardinalDirections
            select paintAndMoveTo(state.Model, newChunk, direction)).ToList();

        plans = 
            from plan in plans
            where plan != null
            orderby plan.TotalLength()
            select plan;

        var ret = plans.FirstOrDefault();

        if (ret == null)
        {
            return null;
        }

        state.Execute(ret, paint: true);

        return ret.ToList();
    }

    private IEnumerable<Command> paintAndMoveTo(
        Model model,
        Chunk newChunk,
        Point forwards)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<Command> Paint(State state, List<Chunk> workingChunks)
    {
        var dirs = Point.CardinalDirections
            .Where(dir => !workingChunks.Any(chunk => chunk.Offset.Equals(Offset + 3 * dir)));

        foreach (var dir in dirs)
        {
            overrideModel.Clear();
            var ret = paint(state.Model, dir).ToList();
            if (ret.Count(command => command.Type == CommandType.Fill) == TargetPointsCount)
            {
                return ret;
            }
        }

        throw new Exception($"Can't paint chunk {GetCenter()}");
    }

        //if (target == null && !forwards.Equals(Point.CardinalDirections[0]))
        //{
        //    return null;
        //}
        //else if (target != null)
        //{
        //    var exitPoint = this.GetCenter() + forwards + forwards;
        //    if (!model.InBounds(exitPoint) || model.Get(exitPoint))
        //    {
        //        return null;
        //    }
        //}

    //overrideModel.Clear();

    //var paintMoves = target == null ? 
    //    new List<Command>() : 
    //    paint(model, forwards).ToList();

    //var gotoMoves = move(
    //    model,
    //    this.GetCenter() + ((target == null) ? Point.Zero : forwards + forwards),
    //    newChunk.GetCenter(),
    //    forwards);

    //if (gotoMoves == null ||
    //    paintMoves.Count(command => command.Type == CommandType.Fill) != TargetPointsCount)
    //{
    //    return null;
    //}

    //return paintMoves.Concat(gotoMoves);

    private IEnumerable<Command> paint(Model model, Point forwards)
    {
        var backwards = -forwards;

        if (model.InBounds(this.GetCenter() + backwards))
        {
            yield return new Command(CommandType.SMove) { P1 = backwards };
            foreach (var command in tryFill(model, backwards, forwards))
            {
                yield return command;
            }

            yield return new Command(CommandType.SMove) { P1 = forwards };
            foreach (var command in tryFill(model, Point.Zero, forwards))
            {
                yield return command;
            }
        }

        yield return new Command(CommandType.SMove) { P1 = forwards };
        foreach (var command in tryFill(model, forwards, forwards))
        {
            yield return command;
        }

        yield return new Command(CommandType.SMove) { P1 = forwards };
        foreach (var command in tryFill(model, forwards + forwards, forwards))
        {
            yield return command;
        }
    }

    public bool IsFillable(Model model)
    {
        return paintable(model) == TargetPointsCount;
    }

    public int paintable(Model model)
    { 
        bool done;
        var painted = 0;

        overrideModel.Clear();

        do
        {
            done = true;
            for (var x = 0; x < 3; ++x)
            {
                for (var y = 0; y < 3; ++y)
                {
                    for (var z = 0; z < 3; ++z)
                    {
                        var p = Offset + new Point(x, y, z);

                        if (target.InBounds(p) &&
                            target.Get(p) && 
                            !get(model, p) &&
                            (p.Y == 0 || Point.CardinalDirections.Any(d => get(model, p + d))))
                        {
                            ++painted;
                            set(p);
                            done = false;
                        }
                    }
                }
            }
        } while (!done);

        return painted;
    }

    private IEnumerable<Command> tryFill(Model model, Point offset, Point forwards)
    {
        bool done;

        do
        {
            done = true;
            foreach (var dir in Point.NearPoints)
            {
                var p = this.GetCenter() + offset + dir;

                if (isInRange(p) &&
                    target.InBounds(p) &&
                    target.Get(p) &&
                    !get(model, p))
                {
                    if (
                        !dir.Equals(forwards) &&
                        (p.Y == 0 || Point.CardinalDirections.Any(d => get(model, p + d))))
                    {
                        yield return new Command(CommandType.Fill) { P1 = dir };
                        set(p);
                        done = false;
                    }
                }
            }
        } while (!done);
    }
}