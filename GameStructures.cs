using GameOffsets.Natives;
using GameOffsets.Objects.UiElement;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Atlas
{
    /// <summary>
    ///     Struct UiElement.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct UiElement
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;

        private static readonly Func<uint, bool> IsVisibleBit = UiElementBaseFuncs.IsVisibleChecker;
        private const int MaxChildren = 10000;

        private static int CountFromSnapshot(in StdVector vector)
        {
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero)
                return 0;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0)
                return 0;

            int stride = IntPtr.Size;
            if ((bytes % stride) != 0)
                return 0;

            long count = bytes / stride;
            if (count <= 0 || count > MaxChildren)
                return 0;

            return (int)count;
        }

        /// <summary>
        ///     Number of children in the element's StdVector
        /// </summary>
        public readonly int Length
        {
            get
            {
                var vector = UiElementBase.ChildrensPtr;

                return CountFromSnapshot(vector);
            }
        }

        /// <summary>
        ///     True if this element (and its own bit) is visible.
        /// </summary>
        public readonly bool IsVisible => IsVisibleBit(UiElementBase.Flags);

        /// <summary>
        ///     
        /// </summary>
        /// <param name="index"></param>
        /// <returns>the child UiElement at index</returns>
        public readonly UiElement GetChild(int index)
        {
            var address = GetChildAddress(index);

            return address == IntPtr.Zero ? default : Atlas.Read<UiElement>(address);
        }

        /// <summary>
        ///     
        /// </summary>
        /// <param name="index"></param>
        /// <returns>the address of the child UiElement at index</returns>
        public readonly IntPtr GetChildAddress(int index)
        {
            var vector = UiElementBase.ChildrensPtr;
            int count = CountFromSnapshot(in vector);
            if ((uint)index >= (uint)count)
                return IntPtr.Zero;

            int stride = IntPtr.Size;
            var slot = IntPtr.Add(vector.First, index * stride);

            return Atlas.Read<IntPtr>(slot);
        }

        /// <summary>
        ///     Reinterprets the child at index as an AtlasNode.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public readonly AtlasNode GetAtlasNode(int index)
        {
            var address = GetChildAddress(index);

            return address == IntPtr.Zero ? default : Atlas.Read<AtlasNode>(address);
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct AtlasMapOffsets
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;
        [FieldOffset(0x510)] public StdVector AtlasNodes;
        [FieldOffset(0x528)] public StdVector AtlasNodeConnections;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtlasNodeEntry
    {
        public StdTuple2D<int> GridPosition;
        public IntPtr UiElementPtr;
        public IntPtr UnknownPtr;
        private readonly long _pad_0x18;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtlasNodeConnections
    {
        public StdTuple2D<int> GridPosition;
        public StdTuple2D<int> Connection1;
        public StdTuple2D<int> Connection2;
        public StdTuple2D<int> Connection3;
        public StdTuple2D<int> Connection4;
    }

    /// <summary>
    ///     Struct AtlasNode.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct AtlasNode
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;
        [FieldOffset(0x270)] public IntPtr NodeNameAddress;
        [FieldOffset(0x290)] public AtlasNodeState Flags;
        [FieldOffset(0x293)] public byte BiomeId;

        public readonly bool IsAccessible => Flags.HasFlag(AtlasNodeState.AccessibleNow);
        public readonly bool IsNotAccessible => !IsAccessible;
        public readonly bool IsCompleted => Flags.HasFlag(AtlasNodeState.CompletedBase);
        public static bool IsFailedAttempt => false;

        public readonly string MapName
        {
            get
            {
                var buffer = Atlas.Read<IntPtr>(NodeNameAddress + 0x8);

                return Atlas.ReadWideString(buffer, 64);
            }
        }
    }

    /// <summary>
    ///     Enum AtlasNodeState.
    /// </summary>
    [Flags]
    public enum AtlasNodeState : ushort
    {
        None                = 0x0000,

        AccessibleNow       = 0x0001,
        CompletedBase       = 0x0002,
    }
}