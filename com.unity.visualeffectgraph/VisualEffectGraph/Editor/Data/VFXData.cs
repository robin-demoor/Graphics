using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using System.Linq;

namespace UnityEditor.VFX
{
    abstract class VFXData : VFXModel
    {
        public abstract VFXDataType type { get; }

        public IEnumerable<VFXContext> owners
        {
            get { return m_Owners; }
        }

        public static VFXData CreateDataType(VFXDataType type)
        {
            switch (type)
            {
                case VFXDataType.kParticle:     return ScriptableObject.CreateInstance<VFXDataParticle>();
                case VFXDataType.kSpawnEvent:   return ScriptableObject.CreateInstance<VFXDataSpawnEvent>();
                default:                        return null;
            }
        }

        public override void OnEnable()
        {
            base.OnEnable();

            if (m_Owners == null)
                m_Owners = new List<VFXContext>();

            if (m_TestId == 0)
                m_TestId = UnityEngine.Random.Range(0, int.MaxValue);
        }

        // Never call this directly ! Only context must call this through SetData
        public void OnContextAdded(VFXContext context)
        {
            m_Owners.Add(context);
        }

        // Never call this directly ! Only context must call this through SetData
        public void OnContextRemoved(VFXContext context)
        {
            m_Owners.Remove(context);
        }

        public bool IsCurrentAttributeRead(VFXAttribute attrib)                        { return (GetAttributeMode(attrib) & VFXAttributeMode.ReadCurrent) != 0; }
        public bool IsCurrentAttributeWritten(VFXAttribute attrib)                     { return (GetAttributeMode(attrib) & VFXAttributeMode.WriteCurrent) != 0; }

        public bool IsCurrentAttributeRead(VFXAttribute attrib, VFXContext context)     { return (GetAttributeMode(attrib, context) & VFXAttributeMode.ReadCurrent) != 0; }
        public bool IsCurrentAttributeWritten(VFXAttribute attrib, VFXContext context) { return (GetAttributeMode(attrib, context) & VFXAttributeMode.WriteCurrent) != 0; }

        public bool IsAttributeUsed(VFXAttribute attrib)                                { return GetAttributeMode(attrib) != VFXAttributeMode.None; }
        public bool IsAttributeUsed(VFXAttribute attrib, VFXContext context)            { return GetAttributeMode(attrib, context) != VFXAttributeMode.None; }

        public bool IsCurrentAttributeUsed(VFXAttribute attrib)                         { return (GetAttributeMode(attrib) & VFXAttributeMode.ReadWriteCurrent) != 0; }

        public bool IsSourceAttributeUsed(VFXAttribute attrib)                          { return (GetAttributeMode(attrib) & VFXAttributeMode.ReadSource) != 0; }

        public bool IsAttributeLocal(VFXAttribute attrib)                               { return m_LocalCurrentAttributes.Contains(attrib); }
        public bool IsAttributeStored(VFXAttribute attrib)                              { return m_StoredCurrentAttributes.ContainsKey(attrib); }

        public VFXAttributeMode GetAttributeMode(VFXAttribute attrib, VFXContext context)
        {
            Dictionary<VFXContext, VFXAttributeMode> contexts;
            if (m_AttributesToContexts.TryGetValue(attrib, out contexts))
            {
                foreach (var c in contexts)
                    if (c.Key == context)
                        return c.Value;
            }

            return VFXAttributeMode.None;
        }

        public VFXAttributeMode GetAttributeMode(VFXAttribute attrib)
        {
            VFXAttributeMode mode = VFXAttributeMode.None;
            Dictionary<VFXContext, VFXAttributeMode> contexts;
            if (m_AttributesToContexts.TryGetValue(attrib, out contexts))
            {
                foreach (var context in contexts)
                    mode |= context.Value;
            }

            return mode;
        }

        public int GetNbAttributes()
        {
            return m_AttributesToContexts.Count;
        }

        public IEnumerable<VFXAttributeInfo> GetAttributes()
        {
            foreach (var attrib in m_AttributesToContexts)
            {
                VFXAttributeInfo info;
                info.attrib = attrib.Key;
                info.mode = VFXAttributeMode.None;

                foreach (var context in attrib.Value)
                    info.mode |= context.Value;

                yield return info;
            }
        }

        private struct VFXAttributeInfoContext
        {
            public VFXAttributeInfo[] attributes;
            public VFXContext context;
        }

        public void CollectAttributes()
        {
            m_ContextsToAttributes.Clear();
            m_AttributesToContexts.Clear();

            bool changed = true;
            while (changed)
            {
                var attributeContexts = new List<VFXAttributeInfoContext>();
                foreach (var context in owners)
                {
                    var attributes = Enumerable.Empty<VFXAttributeInfo>();
                    attributes = attributes.Concat(context.attributes);
                    foreach (var block in context.activeChildrenWithImplicit)
                        attributes = attributes.Concat(block.attributes);

                    var mapper = context.GetExpressionMapper(context.ownedType == VFXDataType.kParticle ? VFXDeviceTarget.GPU : VFXDeviceTarget.CPU);
                    foreach (var exp in mapper.expressions)
                        attributes = attributes.Concat(CollectInputAttributes(exp));

                    attributeContexts.Add(new VFXAttributeInfoContext
                    {
                        attributes = attributes.ToArray(),
                        context = context
                    });
                }

                for (int i = 0; i < m_Owners.Count; ++i)
                {
                    attributeContexts.Add(new VFXAttributeInfoContext
                    {
                        attributes = m_Owners[i].optionalAttributes.ToArray(),
                        context = m_Owners[i]
                    });
                }

                changed = false;
                foreach (var context in attributeContexts)
                {
                    foreach (var attribute in context.attributes)
                    {
                        if (AddAttribute(context.context, attribute))
                        {
                            changed = true;
                        }
                    }
                }
            }

            ProcessAttributes();

            //TMP Debug only
            DebugLogAttributes();
        }

        protected bool HasImplicitInit(VFXAttribute attrib)
        {
            return (attrib.Equals(VFXAttribute.Seed)
                    || attrib.Equals(VFXAttribute.ParticleId)
                    || attrib.Equals(VFXAttribute.Phase));
        }

        private void ProcessAttributes()
        {
            m_StoredCurrentAttributes.Clear();
            m_LocalCurrentAttributes.Clear();

            int nbOwners = m_Owners.Count;
            if (nbOwners > 16)
                throw new InvalidOperationException(string.Format("Too many contexts that use particle data {0} > 16", nbOwners));

            foreach (var kvp in m_AttributesToContexts)
            {
                bool local = false;
                var attribute = kvp.Key;
                int key = 0;

                bool onlyInit = true;
                bool onlyOutput = true;
                bool onlyUpdateRead = true;
                bool onlyUpdateWrite = true;
                bool needsSpecialInit = HasImplicitInit(attribute);
                bool writtenInInit = needsSpecialInit;

                foreach (var kvp2 in kvp.Value)
                {
                    if (kvp2.Value == VFXAttributeMode.None)
                    {
                        throw new InvalidOperationException("Unexpected attribute mode : " + attribute);
                    }

                    if (kvp2.Value == VFXAttributeMode.ReadSource)
                    {
                        continue;
                    }

                    var context = kvp2.Key;
                    if (context.contextType != VFXContextType.kInit)
                        onlyInit = false;
                    if (context.contextType != VFXContextType.kOutput)
                        onlyOutput = false;
                    if (context.contextType != VFXContextType.kUpdate)
                    {
                        onlyUpdateRead = false;
                        onlyUpdateWrite = false;
                    }
                    else
                    {
                        if ((kvp2.Value & VFXAttributeMode.ReadCurrent) != 0)
                            onlyUpdateWrite = false;
                        if ((kvp2.Value & VFXAttributeMode.WriteCurrent) != 0)
                            onlyUpdateRead = false;
                    }

                    if (context.contextType != VFXContextType.kInit) // Init isnt taken into account for key computation
                    {
                        int shift = m_Owners.IndexOf(context) << 1;
                        int value = 0;
                        if ((kvp2.Value & VFXAttributeMode.ReadCurrent) != 0)
                            value |= 0x01;
                        if (((kvp2.Value & VFXAttributeMode.WriteCurrent) != 0) && context.contextType == VFXContextType.kUpdate)
                            value |= 0x02;
                        key |= (value << shift);
                    }
                    else if ((kvp2.Value & VFXAttributeMode.WriteCurrent) != 0)
                        writtenInInit = true;
                }

                if ((key & ~0xAAAAAAAA) == 0) // no read
                    local = true;
                if (onlyUpdateWrite || onlyInit || (!needsSpecialInit && (onlyUpdateRead || onlyOutput))) // no shared atributes
                    local = true;
                if (!writtenInInit && (key & 0xAAAAAAAA) == 0) // no write mask
                    local = true;

                if (local)
                    m_LocalCurrentAttributes.Add(attribute);
                else
                    m_StoredCurrentAttributes.Add(attribute, key);
            }
        }

        public virtual void GenerateAttributeLayout()                                   {}

        public virtual string GetAttributeDataDeclaration(VFXAttributeMode mode)        { throw new NotImplementedException(); }
        public virtual string GetLoadAttributeCode(VFXAttribute attrib)                 { throw new NotImplementedException(); }
        public virtual string GetStoreAttributeCode(VFXAttribute attrib, string value)  { throw new NotImplementedException(); }

        private bool AddAttribute(VFXContext context, VFXAttributeInfo attribInfo)
        {
            if (attribInfo.mode == VFXAttributeMode.None)
                throw new ArgumentException("Cannot add an attribute without mode");

            //if ((attribInfo.mode & VFXAttributeMode.Write) != 0 && context.contextType == VFXContextType.kOutput)
            //    throw new ArgumentException("Output contexts cannot write attributes");

            Dictionary<VFXAttribute, VFXAttributeMode> attribs;
            if (!m_ContextsToAttributes.TryGetValue(context, out attribs))
            {
                attribs = new Dictionary<VFXAttribute, VFXAttributeMode>();
                m_ContextsToAttributes.Add(context, attribs);
            }

            var attrib = attribInfo.attrib;
            var mode = attribInfo.mode;

            bool hasChanged = false;
            if (attribs.ContainsKey(attrib))
            {
                var oldMode = attribs[attrib];
                mode |= attribs[attrib];
                if (mode != oldMode)
                {
                    attribs[attrib] = mode;
                    hasChanged = true;
                }
            }
            else
            {
                attribs[attrib] = mode;
                hasChanged = true;
            }

            if (hasChanged)
            {
                Dictionary<VFXContext, VFXAttributeMode> contexts;
                if (!m_AttributesToContexts.TryGetValue(attrib, out contexts))
                {
                    contexts = new Dictionary<VFXContext, VFXAttributeMode>();
                    m_AttributesToContexts.Add(attrib, contexts);
                }
                contexts[context] = mode;
            }

            return hasChanged;
        }

        // Collect attribute expressions recursively
        private IEnumerable<VFXAttributeInfo> CollectInputAttributes(VFXExpression exp)
        {
            if (exp.Is(VFXExpression.Flags.PerElement)) // Testing per element allows to early out as it is propagated
            {
                foreach (var info in exp.GetNeededAttributes())
                    yield return info;

                foreach (var parent in exp.parents)
                {
                    foreach (var info in CollectInputAttributes(parent))
                        yield return info;
                }
            }
        }

        private void DebugLogAttributes()
        {
            var builder = new StringBuilder();

            builder.AppendLine(string.Format("Attributes for data {0} of type {1}", GetHashCode(), GetType()));
            foreach (var context in owners)
            {
                Dictionary<VFXAttribute, VFXAttributeMode> attributeInfos;
                if (m_ContextsToAttributes.TryGetValue(context, out attributeInfos))
                {
                    builder.AppendLine(string.Format("\tContext {1} {0}", context.GetHashCode(), context.contextType));
                    foreach (var kvp in attributeInfos)
                        builder.AppendLine(string.Format("\t\tAttribute {0} {1} {2}", kvp.Key.name, kvp.Key.type, kvp.Value));
                }
            }

            if (m_StoredCurrentAttributes.Count > 0)
            {
                builder.AppendLine("--- STORED CURRENT ATTRIBUTES ---");
                foreach (var kvp in m_StoredCurrentAttributes)
                    builder.AppendLine(string.Format("\t\tAttribute {0} {1} {2}", kvp.Key.name, kvp.Key.type, kvp.Value));
            }

            if (m_AttributesToContexts.Count > 0)
            {
                builder.AppendLine("--- LOCAL CURRENT ATTRIBUTES ---");
                foreach (var attrib in m_LocalCurrentAttributes)
                    builder.AppendLine(string.Format("\t\tAttribute {0} {1}", attrib.name, attrib.type));
            }

            Debug.Log(builder.ToString());
        }

        [SerializeField]
        protected List<VFXContext> m_Owners;

        //[NonSerialized]
        public int m_TestId;

        [NonSerialized]
        protected Dictionary<VFXContext, Dictionary<VFXAttribute, VFXAttributeMode>> m_ContextsToAttributes = new Dictionary<VFXContext, Dictionary<VFXAttribute, VFXAttributeMode>>();
        [NonSerialized]
        protected Dictionary<VFXAttribute, Dictionary<VFXContext, VFXAttributeMode>> m_AttributesToContexts = new Dictionary<VFXAttribute, Dictionary<VFXContext, VFXAttributeMode>>();

        [NonSerialized]
        protected Dictionary<VFXAttribute, int> m_StoredCurrentAttributes = new Dictionary<VFXAttribute, int>();
        [NonSerialized]
        protected HashSet<VFXAttribute> m_LocalCurrentAttributes = new HashSet<VFXAttribute>();
    }
}
