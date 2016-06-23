﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace BizHawk.Emulation.Common.BizInvoke
{
	public static class BizInvoker
	{
		/// <summary>
		/// holds information about a proxy implementation, including type and setup hooks
		/// </summary>
		private class InvokerImpl
		{
			public Type ImplType;
			public List<Action<object, IImportResolver>> Hooks;

			public object Create(IImportResolver dll)
			{
				var ret = Activator.CreateInstance(ImplType);
				foreach (var f in Hooks)
					f(ret, dll);
				return ret;
			}
		}

		/// <summary>
		/// dictionary of all generated proxy implementations and their basetypes
		/// </summary>
		private static IDictionary<Type, InvokerImpl> Impls = new Dictionary<Type, InvokerImpl>();

		/// <summary>
		/// the assembly that all proxies are placed in
		/// </summary>
		private static readonly AssemblyBuilder ImplAssemblyBuilder;
		/// <summary>
		/// the module that all proxies are placed in
		/// </summary>
		private static readonly ModuleBuilder ImplModuleBilder;

		static BizInvoker()
		{
			var aname = new AssemblyName("BizInvokeProxyAssembly");
			ImplAssemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(aname, AssemblyBuilderAccess.Run);
			ImplModuleBilder = ImplAssemblyBuilder.DefineDynamicModule("BizInvokerModule");
		}

		/// <summary>
		/// get an implementation proxy for an interop class
		/// </summary>
		public static T GetInvoker<T>(IImportResolver dll)
			where T : class
		{
			InvokerImpl impl;
			lock (Impls)
			{
				var baseType = typeof(T);
				if (!Impls.TryGetValue(baseType, out impl))
				{
					impl = CreateProxy(baseType);
					Impls.Add(baseType, impl);
				}
			}
			return (T)impl.Create(dll);
		}

		private static InvokerImpl CreateProxy(Type baseType)
		{
			if (baseType.IsSealed)
				throw new InvalidOperationException("Can't proxy a sealed type");
			if (!baseType.IsPublic)
				// the proxy type will be in a new assembly, so public is required here
				throw new InvalidOperationException("Type must be public");

			var baseConstructor = baseType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
			if (baseConstructor == null)
				throw new InvalidOperationException("Base type must have a zero arg constructor");

			var baseMethods = baseType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
				.Select(m => new
				{
					Info = m,
					Attr = m.GetCustomAttributes(true).OfType<BizImportAttribute>().FirstOrDefault()
				})
				.Where(a => a.Attr != null)
				.ToList();

			if (baseMethods.Count == 0)
				throw new InvalidOperationException("Couldn't find any [BizImport] methods to proxy");

			{
				var uo = baseMethods.FirstOrDefault(a => !a.Info.IsVirtual || a.Info.IsFinal);
				if (uo != null)
					throw new InvalidOperationException("Method " + uo.Info.Name + " cannot be overriden!");

				// there's no technical reason to disallow this, but we wouldn't be doing anything
				// with the base implementation, so it's probably a user error
				var na = baseMethods.FirstOrDefault(a => !a.Info.IsAbstract);
				if (na != null)
					throw new InvalidOperationException("Method " + na.Info.Name + " is not abstract!");
			}

			// hooks that will be run on the created proxy object
			var postCreateHooks = new List<Action<object, IImportResolver>>();

			var type = ImplModuleBilder.DefineType("Bizhawk.BizInvokeProxy", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed, baseType);

			foreach (var mi in baseMethods)
			{
				var entryPointName = mi.Attr.EntryPoint ?? mi.Info.Name;

				var hook = mi.Attr.Compatibility
					? ImplementMethodDelegate(type, mi.Info, mi.Attr.CallingConvention, entryPointName)
					: ImplementMethodCalli(type, mi.Info, mi.Attr.CallingConvention, entryPointName);

				postCreateHooks.Add(hook);
			}

			return new InvokerImpl
			{
				Hooks = postCreateHooks,
				ImplType = type.CreateType()
			};
		}

		/// <summary>
		/// create a method implementation that uses GetDelegateForFunctionPointer internally
		/// </summary>
		private static Action<object, IImportResolver> ImplementMethodDelegate(TypeBuilder type, MethodInfo baseMethod, CallingConvention nativeCall, string entryPointName)
		{
			var paramInfos = baseMethod.GetParameters();
			var paramTypes = paramInfos.Select(p => p.ParameterType).ToArray();
			var returnType = baseMethod.ReturnType;

			// create the delegate type
			var delegateType = type.DefineNestedType("DelegateType" + baseMethod.Name,
				TypeAttributes.Class | TypeAttributes.NestedPrivate | TypeAttributes.Sealed, typeof(MulticastDelegate));
			var delegateCtor = delegateType.DefineConstructor(
				MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
				CallingConventions.Standard, new Type[] { typeof(object), typeof(IntPtr) });
			delegateCtor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
			var delegateInvoke = delegateType.DefineMethod("Invoke",
				MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual, returnType, paramTypes);
			delegateInvoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
			// add the [UnmanagedFunctionPointer] to the delegate so interop will know how to call it
			var attr = new CustomAttributeBuilder(typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) }), new object[] { nativeCall });
			delegateType.SetCustomAttribute(attr);

			// define a field on the class to hold the delegate
			var field = type.DefineField("DelegateField" + baseMethod.Name, delegateType,
				FieldAttributes.Public);

			var method = type.DefineMethod(baseMethod.Name, MethodAttributes.Virtual | MethodAttributes.Public,
				CallingConventions.HasThis, returnType, paramTypes);
			var il = method.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, field);
			for (int i = 0; i < paramTypes.Length; i++)
				il.Emit(OpCodes.Ldarg, (short)(i + 1));
			il.Emit(OpCodes.Callvirt, delegateInvoke);

			il.Emit(OpCodes.Ret);

			type.DefineMethodOverride(method, baseMethod);

			return (o, dll) =>
			{
				var entryPtr = dll.SafeResolve(entryPointName);
				var interopDelegate = Marshal.GetDelegateForFunctionPointer(entryPtr, delegateType.CreateType());
				o.GetType().GetField(field.Name).SetValue(o, interopDelegate);
			};
		}

		/// <summary>
		/// create a method implementation that uses calli internally
		/// </summary>
		private static Action<object, IImportResolver> ImplementMethodCalli(TypeBuilder type, MethodInfo baseMethod, CallingConvention nativeCall, string entryPointName)
		{
			var paramInfos = baseMethod.GetParameters();
			var paramTypes = paramInfos.Select(p => p.ParameterType).ToArray();
			var nativeParamTypes = new List<Type>();
			var returnType = baseMethod.ReturnType;
			if (returnType != typeof(void) && !returnType.IsPrimitive)
				throw new InvalidOperationException("Only primitive return types are supported");

			// define a field on the type to hold the entry pointer
			var field = type.DefineField("EntryPtrField" + baseMethod.Name, typeof(IntPtr),
				FieldAttributes.Public);

			var method = type.DefineMethod(baseMethod.Name, MethodAttributes.Virtual | MethodAttributes.Public,
				CallingConventions.HasThis, returnType, paramTypes);


			var il = method.GetILGenerator();
			for (int i = 0; i < paramTypes.Length; i++)
			{
				// arg 0 is this, so + 1
				nativeParamTypes.Add(EmitParamterLoad(il, i + 1, paramTypes[i]));
			}
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, field);
			il.EmitCalli(OpCodes.Calli, nativeCall, returnType, nativeParamTypes.ToArray());

			// either there's a primitive on the stack and we're expected to return that primitive,
			// or there's nothing on the stack and we're expected to return nothing
			il.Emit(OpCodes.Ret);

			type.DefineMethodOverride(method, baseMethod);

			return (o, dll) =>
			{
				var entryPtr = dll.SafeResolve(entryPointName);
				o.GetType().GetField(field.Name).SetValue(o, entryPtr);
			};
		}

		/// <summary>
		/// load an IntPtr constant in an il stream
		/// </summary>
		private static void LoadConstant(ILGenerator il, IntPtr p)
		{
			if (p == IntPtr.Zero)
				il.Emit(OpCodes.Ldc_I4_0);
			else if (IntPtr.Size == 4)
				il.Emit(OpCodes.Ldc_I4, (int)p);
			else
				il.Emit(OpCodes.Ldc_I8, (long)p);
			il.Emit(OpCodes.Conv_I);
		}

		/// <summary>
		/// load a UIntPtr constant in an il stream
		/// </summary>
		private static void LoadConstant(ILGenerator il, UIntPtr p)
		{
			if (p == UIntPtr.Zero)
				il.Emit(OpCodes.Ldc_I4_0);
			else if (UIntPtr.Size == 4)
				il.Emit(OpCodes.Ldc_I4, (int)p);
			else
				il.Emit(OpCodes.Ldc_I8, (long)p);
			il.Emit(OpCodes.Conv_U);
		}

		/// <summary>
		/// emit a single parameter load with unmanaged conversions
		/// </summary>
		private static Type EmitParamterLoad(ILGenerator il, int idx, Type type)
		{
			if (type.IsGenericType)
				throw new InvalidOperationException("Generic types not supported");
			if (type.IsByRef)
			{
				var et = type.GetElementType();
				if (!et.IsPrimitive)
					throw new InvalidOperationException("Only refs of primitive types are supported!");
				var loc = il.DeclareLocal(type, true);
				il.Emit(OpCodes.Ldarg, (short)idx);
				il.Emit(OpCodes.Dup);
				il.Emit(OpCodes.Stloc, loc);
				il.Emit(OpCodes.Conv_I);
				return typeof(IntPtr);
			}
			else if (type.IsArray)
			{
				var et = type.GetElementType();
				if (!et.IsPrimitive)
					throw new InvalidOperationException("Only arrays of primitive types are supported!");

				// these two cases aren't too hard to add
				if (type.GetArrayRank() > 1)
					throw new InvalidOperationException("Multidimensional arrays are not supported!");
				if (type.Name.Contains('*'))
					throw new InvalidOperationException("Only 0-based 1-dimensional arrays are supported!");

				var loc = il.DeclareLocal(type, true);
				var end = il.DefineLabel();
				var isNull = il.DefineLabel();

				il.Emit(OpCodes.Ldarg, (short)idx);
				il.Emit(OpCodes.Brfalse, isNull);

				il.Emit(OpCodes.Ldarg, (short)idx);
				il.Emit(OpCodes.Dup);
				il.Emit(OpCodes.Stloc, loc);
				il.Emit(OpCodes.Ldc_I4_0);
				il.Emit(OpCodes.Ldelema, et);
				il.Emit(OpCodes.Conv_I);
				il.Emit(OpCodes.Br, end);

				il.MarkLabel(isNull);
				LoadConstant(il, IntPtr.Zero);
				il.MarkLabel(end);

				return typeof(IntPtr);
			}
			else if (typeof(Delegate).IsAssignableFrom(type))
			{
				var mi = typeof(Marshal).GetMethod("GetFunctionPointerForDelegate", new[] { typeof(Delegate) });
				var end = il.DefineLabel();
				var isNull = il.DefineLabel();

				il.Emit(OpCodes.Ldarg, (short)idx);
				il.Emit(OpCodes.Brfalse, isNull);

				il.Emit(OpCodes.Ldarg, (short)idx);
				il.Emit(OpCodes.Call, mi);
				il.Emit(OpCodes.Br, end);

				il.MarkLabel(isNull);
				LoadConstant(il, IntPtr.Zero);
				il.MarkLabel(end);
				return typeof(IntPtr);
			}
			else if (type.IsPrimitive)
			{
				il.Emit(OpCodes.Ldarg, (short)idx);
				return type;
			}
			else
			{
				throw new InvalidOperationException("Unrecognized parameter type!");
			}
		}
	}

	/// <summary>
	/// mark an abstract method to be proxied by BizInvoker
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	public class BizImportAttribute : Attribute
	{
		public CallingConvention CallingConvention
		{
			get { return _callingConvention; }
		}
		private readonly CallingConvention _callingConvention;

		/// <summary>
		/// name of entry point; if not given, the method's name is used
		/// </summary>
		public string EntryPoint { get; set; }

		/// <summary>
		/// Use a slower interop that supports more argument types
		/// </summary>
		public bool Compatibility { get; set; }

		/// <summary>
		/// 
		/// </summary>
		/// <param name="c">unmanaged calling convention</param>
		public BizImportAttribute(CallingConvention c)
		{
			_callingConvention = c;
		}
	}
}
