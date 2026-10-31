using MessagePack;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Sync;

// An item broken into separate fields, so two people changing different things never touch each
// other's work. Numbers and rotations travel as how much they changed, so +6 and -2 make +4 in
// either order. Choices (which animation, which effect, shown or hidden) can't be added up, so
// they travel as the new value, and only for the field that changed.
public class SyncValues(float[] numbers, Quaternion[] rotations, string?[] choices)
{
    private const float NumberTolerance = 0.001f;
    private const float RotationTolerance = 0.000001f;

    public float[] Numbers { get; } = numbers;
    public Quaternion[] Rotations { get; } = rotations;
    public string?[] Choices { get; } = choices;

    // What changed from `last`, field by field; every field, as plain values, if there's no `last`.
    public static SyncValueDelta Diff(SyncValues current, SyncValues? last)
    {
        var delta = new SyncValueDelta { Absolute = last is null };

        for(var i = 0; i < current.Numbers.Length; i++)
        {
            var change = last is null ? current.Numbers[i] : current.Numbers[i] - last.Numbers[i];
            if(last is null || MathF.Abs(change) > NumberTolerance)
            {
                delta.NumberFields.Add((ushort)i);
                delta.Numbers.Add(change);
            }
        }

        for(var i = 0; i < current.Rotations.Length; i++)
        {
            if(last is null)
            {
                delta.RotationFields.Add((ushort)i);
                delta.Rotations.Add(current.Rotations[i]);
            }
            else if(!IsSameRotation(current.Rotations[i], last.Rotations[i]))
            {
                delta.RotationFields.Add((ushort)i);
                delta.Rotations.Add(RotationFrom(last.Rotations[i], current.Rotations[i]));
            }
        }

        for(var i = 0; i < current.Choices.Length; i++)
        {
            if(last is null || current.Choices[i] != last.Choices[i])
            {
                delta.ChoiceFields.Add((ushort)i);
                delta.Choices.Add(current.Choices[i]);
            }
        }

        return delta;
    }

    // The values after an update. Null if it doesn't fit these values.
    public static SyncValues? Apply(SyncValues last, SyncValueDelta delta)
    {
        if(!delta.IsWellFormed(last))
            return null;

        var numbers = (float[])last.Numbers.Clone();
        var rotations = (Quaternion[])last.Rotations.Clone();
        var choices = (string?[])last.Choices.Clone();

        for(var i = 0; i < delta.NumberFields.Count; i++)
        {
            var field = delta.NumberFields[i];
            numbers[field] = delta.Absolute ? delta.Numbers[i] : numbers[field] + delta.Numbers[i];
        }

        for(var i = 0; i < delta.RotationFields.Count; i++)
        {
            var field = delta.RotationFields[i];
            rotations[field] = delta.Absolute ? delta.Rotations[i] : Quaternion.Normalize(delta.Rotations[i] * rotations[field]);
        }

        for(var i = 0; i < delta.ChoiceFields.Count; i++)
            choices[delta.ChoiceFields[i]] = delta.Choices[i];

        return new SyncValues(numbers, rotations, choices);
    }

    // The values in a first share or checkpoint, which holds every field in order.
    public static SyncValues? FromAbsolute(SyncValueDelta delta)
    {
        if(!delta.Absolute)
            return null;

        for(var i = 0; i < delta.NumberFields.Count; i++)
        {
            if(delta.NumberFields[i] != i)
                return null;
        }

        for(var i = 0; i < delta.RotationFields.Count; i++)
        {
            if(delta.RotationFields[i] != i)
                return null;
        }

        for(var i = 0; i < delta.ChoiceFields.Count; i++)
        {
            if(delta.ChoiceFields[i] != i)
                return null;
        }

        return new SyncValues([.. delta.Numbers], [.. delta.Rotations], [.. delta.Choices]);
    }

    // `current` with the change from `from` to `to` added on top, so anything changed locally
    // and not sent yet is kept.
    public static SyncValues Rebase(SyncValues current, SyncValues from, SyncValues to)
    {
        var numbers = (float[])current.Numbers.Clone();
        var rotations = (Quaternion[])current.Rotations.Clone();
        var choices = (string?[])current.Choices.Clone();

        for(var i = 0; i < numbers.Length; i++)
            numbers[i] += to.Numbers[i] - from.Numbers[i];

        for(var i = 0; i < rotations.Length; i++)
        {
            if(!IsSameRotation(from.Rotations[i], to.Rotations[i]))
                rotations[i] = Quaternion.Normalize(RotationFrom(from.Rotations[i], to.Rotations[i]) * rotations[i]);
        }

        for(var i = 0; i < choices.Length; i++)
        {
            if(from.Choices[i] != to.Choices[i])
                choices[i] = to.Choices[i];
        }

        return new SyncValues(numbers, rotations, choices);
    }

    public static bool IsSameRotation(Quaternion a, Quaternion b)
        => 1f - MathF.Abs(Quaternion.Dot(a, b)) <= RotationTolerance;

    // The rotation that turns `from` into `to`: to = result * from.
    public static Quaternion RotationFrom(Quaternion from, Quaternion to)
        => Quaternion.Normalize(to * Quaternion.Inverse(from));
}

// The fields of an item that changed.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncValueDelta
{
    private const float MaxNumber = 1_000_000f;
    private const int MaxChoiceLength = 512;

    // Plain values rather than changes: a first share, or the true values to line everyone up.
    public bool Absolute { get; set; }

    public List<ushort> NumberFields { get; set; } = [];
    public List<float> Numbers { get; set; } = [];

    public List<ushort> RotationFields { get; set; } = [];
    public List<Quaternion> Rotations { get; set; } = [];

    public List<ushort> ChoiceFields { get; set; } = [];
    public List<string?> Choices { get; set; } = [];

    // Checked on anything from another player, before it's used.
    public bool IsValid()
    {
        if(NumberFields is null || Numbers is null || RotationFields is null || Rotations is null || ChoiceFields is null || Choices is null)
            return false;

        if(NumberFields.Count != Numbers.Count || RotationFields.Count != Rotations.Count || ChoiceFields.Count != Choices.Count)
            return false;

        foreach(var number in Numbers)
        {
            if(!float.IsFinite(number) || MathF.Abs(number) > MaxNumber)
                return false;
        }

        foreach(var rotation in Rotations)
        {
            if(!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W) || rotation.Length() is < 0.5f or > 1.5f)
                return false;
        }

        foreach(var choice in Choices)
        {
            if(choice is { Length: > MaxChoiceLength })
                return false;
        }

        return true;
    }

    public bool IsWellFormed(SyncValues shape)
    {
        foreach(var field in NumberFields)
        {
            if(field >= shape.Numbers.Length)
                return false;
        }

        foreach(var field in RotationFields)
        {
            if(field >= shape.Rotations.Length)
                return false;
        }

        foreach(var field in ChoiceFields)
        {
            if(field >= shape.Choices.Length)
                return false;
        }

        return true;
    }
}
