﻿using System.Collections.Generic;

namespace BizHawk.Emulation.Common
{
	public interface IDebuggable : IEmulatorService
	{
		/// <summary>
		/// Returns a list of Cpu registers and their current state
		/// </summary>
		/// <returns></returns>
		IDictionary<string, RegisterValue> GetCpuFlagsAndRegisters();

		/// <summary>
		/// Sets a given Cpu register to the given value
		/// </summary>
		/// <param name="register"></param>
		/// <param name="value"></param>
		void SetCpuRegister(string register, int value);

		IMemoryCallbackSystem MemoryCallbacks { get; }

		/// <summary>
		/// Informs the calling code whether or not the given step type is implemented,
		/// if false, a NotImplementedException will be thrown if Step is called with the given value
		/// </summary>
		bool CanStep(StepType type);

		/// <summary>
		/// Advances the core based on the given Step type
		/// </summary>
		void Step(StepType type);
	}

	public class RegisterValue
	{
		public ulong Value { get; private set; }
		public byte BitSize { get; private set; }

		public RegisterValue(ulong val, byte bitSize)
		{
			if (bitSize == 64)
				Value = val;
			else if (bitSize > 64 || bitSize == 0)
				throw new System.ArgumentOutOfRangeException("bitSize", "BitSize must be in 1..64");
			else
				Value = val & (1UL << bitSize) - 1;
			BitSize = bitSize;
		}

		public RegisterValue(bool val)
		{
			Value = val ? 1UL : 0UL;
			BitSize = 1;
		}

		public RegisterValue(byte val)
		{
			Value = val;
			BitSize = 8;
		}

		public RegisterValue(sbyte val)
		{
			Value = (byte)val;
			BitSize = 8;
		}

		public RegisterValue(ushort val)
		{
			Value = val;
			BitSize = 16;
		}

		public RegisterValue(short val)
		{
			Value = (ushort)val;
			BitSize = 16;
		}

		public RegisterValue(uint val)
		{
			Value = val;
			BitSize = 32;
		}

		public RegisterValue(int val)
		{
			Value = (uint)val;
			BitSize = 32;
		}

		public RegisterValue(ulong val)
		{
			Value = val;
			BitSize = 64;
		}

		public RegisterValue(long val)
		{
			Value = (ulong)val;
			BitSize = 64;
		}


		public static implicit operator RegisterValue(bool val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(byte val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(sbyte val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(ushort val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(short val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(uint val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(int val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(ulong val)
		{
			return new RegisterValue(val);
		}

		public static implicit operator RegisterValue(long val)
		{
			return new RegisterValue(val);
		}
	}
}
