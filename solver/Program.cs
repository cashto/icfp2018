using Priority_Queue;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

public class Program
{
    static void Main(string[] args)
    {
    }

    public static List<Command> Solve(Model target)
    {
        var dirs = new List<Point>();
        for (var x = 0; x < 3; ++x)
        {
            for (var z = 0; z < 3; ++z)
            {
                dirs.Add(new Point(x, 0, z));
            }
        }

        var state = new State(new Model(target.Resolution));

        var chunks = findChunks(target).ToList();

        IEnumerable<Command> ans = solveIter(
            state,
            Chunk.Origin,
            chunks,
            new Stack<Chunk>());

        //var first = Chunk.Origin.PaintAndMoveTo(state, chunks.First());
        //var second = chunks.First().PaintAndMoveTo(state, Chunk.Origin);
        //var ans = first.Concat(second);

        var peepholeOptimizers = new List<Func<IEnumerable<Command>, IEnumerable<Command>>>()
        {
            filterNullSMoves,
            combineSMoves,
            splitSMoves,
            packSMoves
        };

        foreach (var optimizer in peepholeOptimizers)
        {
            ans = optimizer(ans).ToList();
        }

        return ans
            .Concat(new List<Command>() { new Command(CommandType.Halt) })
            .ToList();
    }

    private static IEnumerable<Chunk> findChunks(Model model)
    {
        for (var x = 1; x < model.Resolution - 1; x += 3)
        {
            for (var y = 0; y < model.Resolution - 1; y += 3)
            {
                for (var z = 1; z < model.Resolution - 1; z += 3)
                {
                    var chunk = new Chunk(model, new Point(x, y, z));
                    if (chunk.TargetPointsCount > 0)
                    {
                        yield return chunk;
                    }
                }
            }
        }
    }

    private static List<Command> solveIter(
        State state,
        Chunk currentChunk,
        List<Chunk> chunksToVisit,
        Stack<Chunk> visitedChunks)
    {
        Trace.Write($"{currentChunk.Offset} {string.Join(" ", visitedChunks.Select(i => i.Offset.ToString()))}");

        if (!chunksToVisit.Any())
        {
            return currentChunk.PaintAndMoveTo(state.Clone(), Chunk.Origin).ToList();
        }

        var orderedChunksToVisit =
            from chunk in chunksToVisit
            where chunk.Offset.Y == 0 || visitedChunks.Any(c => (c.Offset - chunk.Offset).MDist() == 3)
            orderby (chunk.Offset - currentChunk.Offset).MDist(), chunk.Offset.Y, chunk.Offset.X, chunk.Offset.Z
            select chunk;

        foreach (var chunk in orderedChunksToVisit)
        {
            var stateClone = state.Clone();
            var moves = currentChunk.PaintAndMoveTo(stateClone, chunk);
            if (moves != null && chunk.IsFillable(stateClone.Model))
            {
                var remainingChunks = chunksToVisit.Where(i => !i.Offset.Equals(chunk.Offset)).ToList();
                visitedChunks.Push(currentChunk);
                var tailMoves = solveIter(stateClone, chunk, remainingChunks, visitedChunks);
                visitedChunks.Pop();
                if (tailMoves.Any())
                {
                    return moves.Concat(tailMoves).ToList();
                }
            }
        }

        return new List<Command>();
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
            P = new Point(0, 0, 0),
            Seeds = Enumerable.Range(2, 19).ToList()
        });
    }

    public void Execute(IEnumerable<Command> commands)
    {
        var commandsEnum = commands.GetEnumerator();
        var volatileCells = new VolatileCells();

        while (Bots.Any())
        {
            if (!isWellFormed())
            {
                throw new Exception("State is not well formed");
            }

            Energy += (HighHarmonics ? 30 : 3) + 20 * Bots.Count;

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

            volatileCells.Clear();
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
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Z { get; private set; }

    public static readonly Point[] CardinalDirections = new Point[]
    {
        new Point(1, 0, 0),
        new Point(0, 1, 0),
        new Point(0, 0, 1),
        new Point(-1, 0, 0),
        new Point(0, -1, 0),
        new Point(0, 0, -1)
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

    public void Execute(
        State state,
        Bot bot,
        VolatileCells volatileCells)
    {
        volatileCells.Check(bot.P);

        switch (Type)
        {
            case CommandType.Halt:
                if (!bot.P.Equals(new Point(0, 0, 0)))
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

    private void checkMove(
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
            if (model.Get(p2))
            {
                throw new Exception($"Cannot move: {p} is not empty");
            }

            volatileCells.Check(p2);
        }
    }
}

public class VolatileCells
{
    private HashSet<Point> cells = new HashSet<Point>();

    public void Check(Point p)
    {
        if (!cells.Add(p))
        {
            throw new Exception($"Point {p} is already a volatile cell");
        }
    }

    public void Clear()
    {
        cells.Clear();
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

        state.Execute(ret);

        return ret.ToList();
    }

    private IEnumerable<Command> paintAndMoveTo(
        Model model,
        Chunk newChunk, 
        Point forwards)
    {
        if (target == null && !forwards.Equals(Point.CardinalDirections[0]))
        {
            return null;
        }

        overrideModel.Clear();

        var paintMoves = target == null ? 
            new List<Command>() : 
            paint(model, forwards).ToList();

        var gotoMoves = move(
            model,
            Offset + new Point(1, 1, 1) + ((target == null) ? new Point(0, 0, 0) : forwards + forwards),
            newChunk.Offset + new Point(1, 1, 1),
            forwards);

        if (gotoMoves == null ||
            paintMoves.Count(command => command.Type == CommandType.Fill) != TargetPointsCount)
        {
            return null;
        }

        return paintMoves.Concat(gotoMoves);
    }

    private IEnumerable<Command> paint(Model model, Point forwards)
    {
        var backwards = new Point(-forwards.X, -forwards.Y, -forwards.Z);

        yield return new Command(CommandType.SMove) { P1 = backwards };
        foreach (var command in tryFill(model, backwards, forwards))
        {
            yield return command;
        }

        var offset = backwards;
        for (var i = 0; i < 3; ++i)
        {
            yield return new Command(CommandType.SMove) { P1 = forwards };
            offset = offset + forwards;
            foreach (var command in tryFill(model, offset, forwards))
            {
                yield return command;
            }
        }
    }

    public bool IsFillable(Model model)
    {
        bool done;
        var painted = 0;

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

                        if (target.Get(p) && 
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

        overrideModel.Clear();

        return painted == TargetPointsCount;
    }

    private IEnumerable<Command> tryFill(Model model, Point offset, Point forwards)
    {
        bool done;

        do
        {
            done = true;
            foreach (var dir in Point.NearPoints)
            {
                var p = Offset + offset + dir + new Point(1, 1, 1);

                if (isInRange(p) &&
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

    private IEnumerable<Command> move(Model model, Point src, Point dest, Point forwards)
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
            if (node.P.Equals(dest))
            {
                return getMoves(node).Reverse().Skip(1);
            }

            foreach (var dir in Point.CardinalDirections)
            {
                var p = node.P + dir;
                if (model.InBounds(p) && !get(model, p))
                {
                    var newKey = Tuple.Create(p, dir);
                    if (!costs.ContainsKey(newKey))
                    {
                        var previousCost = costs[Tuple.Create(node.P, node.Dir)];
                        var newCost = previousCost + (dir.Equals(node.Dir) ? 1 : 10);
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
}