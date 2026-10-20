using System;
using System.Numerics;
using Cilbox;
using UnityEngine;

namespace TestCilbox
{
    // Reproductions from hai-vr/CILBOX_QUIRKS.md. Each method is invoked independently,
    // so an interpreter fault in one scenario does not hide the others.
    [Cilboxable]
    public class GistQuirksBehaviour : MonoBehaviour
    {
        public byte[][] jaggedField = { new byte[] { 33 } };
        private int uninitializedPrivateInt;

        public string PrivateDefaultInt()
        {
            return uninitializedPrivateInt.ToString();
        }

        public string JaggedByteField()
        {
            return jaggedField[0][0].ToString();
        }

        public string StructArrayCompoundAssignment()
        {
            Vector2[] positions = { new Vector2(1f, 2f) };
            positions[0] += new Vector2(3f, 4f);
            return positions[0].ToString();
        }

        public string UnsignedFloatConversion()
        {
            uint total = 5;
            float distance = total * 1.5f;
            return distance.ToString();
        }

        public string BoolArrayAccess()
        {
            bool[] flags = new bool[2];
            flags[0] = true;
            return (flags[0] && !flags[1]).ToString();
        }

        public string UninitializedStructLocal()
        {
            Vector2 result;
            result.X = 9f;
            result.Y = 2f;
            return result.ToString();
        }

        public string StructArrayFieldWrite()
        {
            Vector2[] positions = { new Vector2(1f, 2f) };
            positions[0].X = 9f;
            return positions[0].ToString();
        }

        public string JaggedByteArray()
        {
            byte[][] buffers = { new byte[] { 42 } };
            return buffers[0][0].ToString();
        }

        public string NullReferenceCast()
        {
            object input = null;
            string result = (string)input;
            return (result == null).ToString();
        }

        public string AssignableIsInst()
        {
            object input = "hello";
            IComparable comparable = input as IComparable;
            return (comparable != null).ToString();
        }
    }

    // These require separate test runs: invalid metadata can prevent the entire box from loading.
    [Cilboxable]
    public class GistTypeofBehaviour : MonoBehaviour
    {
        public string InspectBoxType()
        {
            return typeof(GistTypeofBehaviour).Name;
        }
    }

    [Cilboxable]
    public class GistStaticBoxFieldBehaviour : MonoBehaviour
    {
        private static GistStaticBoxFieldBehaviour instance;
        public string InspectStaticField()
        {
            return (instance == null).ToString();
        }
    }
}
