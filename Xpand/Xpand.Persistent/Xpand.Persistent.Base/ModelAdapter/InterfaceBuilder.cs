﻿using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.UI.WebControls;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.Utils.CodeGeneration;
using DevExpress.Persistent.Base;
using DevExpress.Utils;
using DevExpress.Utils.Controls;
using DevExpress.Utils.Serializing;
using DevExpress.Web;
using DevExpress.Xpo;
using Xpand.Utils.Helpers;
using Fasterflect;

namespace Xpand.Persistent.Base.ModelAdapter{
    public class InterfaceBuilderData{
        public InterfaceBuilderData(Type componentType){
            ComponentType = componentType;
            BaseInterface = typeof(IModelNodeEnabled);
        }

        public Type ComponentType { get; }

        public Func<DynamicModelPropertyInfo, bool> Act { get; set; }

        [Description("The interface from which all autogenerated interfaces derive. Default is the IModelNodeEnabled")]
        public Type BaseInterface { get; set; }

        public List<Type> ReferenceTypes { get; } = new List<Type>();

        public Type RootBaseInterface { get; set; }

        public bool IsAbstract { get; set; }
    }

    public class InterfaceBuilder{
        readonly ModelInterfaceExtenders _extenders;
        private const string NamePrefix = "IModel";
        readonly List<Type> _usingTypes;
        readonly ReferencesCollector _referencesCollector;
        readonly List<StringBuilder> _builders;
        Dictionary<Type, string> _createdInterfaces;
        string _assemblyName;
        static bool _loadFromPath;
        static bool _fileExistInPath;
        static readonly Dictionary<string, Assembly> _assemblies = new Dictionary<string, Assembly>();

        public InterfaceBuilder(ModelInterfaceExtenders extenders)
            : this(){
            _extenders = extenders;
        }

        InterfaceBuilder(){
            _usingTypes = new List<Type>();
            _builders = new List<StringBuilder>();
            _referencesCollector = new ReferencesCollector();
        }

        static bool? _runtimeMode;

        public static bool RuntimeMode{
            get{
                if (_runtimeMode == null){
                    var devProcceses = new[]{".ExpressApp.ModelEditor", "devenv"};
                    var processName = Process.GetCurrentProcess().ProcessName;
                    var isInProccess = devProcceses.Any(s => processName.IndexOf(s, StringComparison.Ordinal) > -1);
                    _runtimeMode = !isInProccess && LicenseManager.UsageMode != LicenseUsageMode.Designtime;
                }
                return _runtimeMode.Value;
            }
            set { _runtimeMode = value; }
        }

        public ModelInterfaceExtenders Extenders => _extenders;

        public static bool ExternalModelEditor => Process.GetCurrentProcess().ProcessName.IndexOf(".ExpressApp.ModelEditor", StringComparison.Ordinal) >
                                                  -1;

        public static bool LoadFromPath{
            get { return RuntimeMode && _fileExistInPath || _loadFromPath; }
            set { _loadFromPath = value; }
        }

        public static bool SkipAssemblyCleanup { get; set; }

        public Assembly Build(IEnumerable<InterfaceBuilderData> builderDatas, string assemblyFilePath){
            var isAttached = Debugger.IsAttached;

            if (!SkipAssemblyCleanup && ((isAttached || ExternalModelEditor) && File.Exists(assemblyFilePath)) &&
                (!VersionMatch(assemblyFilePath) || IsDevMachine)){
                if (!new FileInfo(assemblyFilePath).IsFileLocked())
                    File.Delete(assemblyFilePath);
            }
            _fileExistInPath = File.Exists(assemblyFilePath);
            if (LoadFromPath && _fileExistInPath && VersionMatch(assemblyFilePath)){
                return Assembly.LoadFile(assemblyFilePath);
            }
            _assemblyName = Path.GetFileNameWithoutExtension(assemblyFilePath) + "";
            if (!RuntimeMode && _assemblies.ContainsKey(_assemblyName + "")){
                return _assemblies[_assemblyName];
            }

            TraceBuildReason(assemblyFilePath);

            _createdInterfaces = new Dictionary<Type, string>();
            var source = string.Join(Environment.NewLine, GetAssemblyVersionCode(), GetCode(builderDatas));
            _usingTypes.Add(typeof(XafApplication));
            _referencesCollector.Add(_usingTypes);
            string[] references = _referencesCollector.References.ToArray();

            var compileAssemblyFromSource = CompileAssemblyFromSource(source, references, false, assemblyFilePath);
            _assemblies.Add(_assemblyName + "", compileAssemblyFromSource);
            return compileAssemblyFromSource;
        }

        public static bool IsDevMachine {
            get {
                try {
                    return  Environment.GetEnvironmentVariable("XpandDevMachine", EnvironmentVariableTarget.User).Change<bool>();
                }
                catch (System.Security.SecurityException) {
                    return false;
                }
            }
        }

        public static bool IsDBUpdater => Process.GetCurrentProcess().ProcessName.StartsWith("DBUpdater"+AssemblyInfo.VSuffix);

        private void TraceBuildReason(string assemblyFilePath){
            var tracing = Tracing.Tracer;
            tracing.LogVerboseSubSeparator("InterfacerBuilder:" + assemblyFilePath);
            tracing.LogVerboseValue("FileExistsInPath", _fileExistInPath);
            tracing.LogVerboseValue("LoadFromPath", LoadFromPath);
            tracing.LogVerboseValue("RuntimeMode", LoadFromPath);
            if (LoadFromPath && _fileExistInPath)
                tracing.LogValue("VersionMatch", VersionMatch(assemblyFilePath));
        }

        bool VersionMatch(string assemblyFilePath){
            return FileVersionInfo.GetVersionInfo(assemblyFilePath).FileVersion ==
                   GetType().Assembly.GetName().Version.ToString();
        }

        protected Assembly LoadFromDomain(string assemblyFilePath){
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            string fileName = Path.GetFileName(assemblyFilePath);
            foreach (var assembly in assemblies){
                if (!(assembly.IsDynamic()) && Path.GetFileName(assembly.Location) == fileName){
                    return assembly;
                }
            }
            throw new NotImplementedException(assemblyFilePath);
        }

        private static Assembly CompileAssemblyFromSource(String source, String[] references, Boolean isDebug,
            String assemblyFile){
            if (!String.IsNullOrEmpty(assemblyFile)){
                var directoryName = Path.GetDirectoryName(assemblyFile) + "";
                if (!Directory.Exists(directoryName)){
                    Directory.CreateDirectory(directoryName);
                }
            }
            CompilerParameters compilerParameters = GetCompilerParameters(references, isDebug, assemblyFile);
            CodeDomProvider codeProvider = CodeDomProvider.CreateProvider("CSharp");
            CompilerResults compilerResults = codeProvider.CompileAssemblyFromSource(compilerParameters, source);
            if (compilerResults.Errors.Count > 0){
                RaiseCompilerException(source, compilerResults, assemblyFile);
            }
            return compilerResults.CompiledAssembly;
        }

        private static void RaiseCompilerException(String source, CompilerResults compilerResults, string assemblyFile){
            throw new CompilerErrorException(compilerResults, source,
                "Assembly=" + assemblyFile + Environment.NewLine + compilerResults.AggregateErrors());
        }

        private static CompilerParameters GetCompilerParameters(String[] references, Boolean isDebug,
            String assemblyFile){
            var compilerParameters = new CompilerParameters();
            compilerParameters.ReferencedAssemblies.AddRange(references);
            if (String.IsNullOrEmpty(assemblyFile)){
                compilerParameters.GenerateInMemory = !isDebug;
            }
            else{
                compilerParameters.OutputAssembly = RuntimeMode ? assemblyFile : null;
            }
            if (isDebug){
                compilerParameters.TempFiles = new TempFileCollection(Environment.GetEnvironmentVariable("TEMP"), true);
                compilerParameters.IncludeDebugInformation = true;
            }
            return compilerParameters;
        }

        string GetAssemblyVersionCode(){
            var assemblyVersion = ReflectionHelper.GetAssemblyVersion(GetType().Assembly);
            return string.Format(@"[assembly: {1}(""{0}"")]", assemblyVersion,
                TypeToString(typeof(AssemblyVersionAttribute)));
        }

        public static string GetTempDirectory(){
            var directory = Path.Combine(Environment.GetEnvironmentVariable("temp") + "", XpandAssemblyInfo.Version);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            return directory;
        }

        string GetCode(IEnumerable<InterfaceBuilderData> builderDatas){
            foreach (var data in builderDatas){
                _usingTypes.AddRange(data.ReferenceTypes);
                CreateInterface(data.ComponentType, data.Act, data.BaseInterface, data.IsAbstract,
                    data.RootBaseInterface);
            }
            string usings = _referencesCollector.Usings.Aggregate<string, string>(null,
                (current, @using) => current + ("using " + @using + ";" + Environment.NewLine));
            return usings + Environment.NewLine +
                   string.Join(Environment.NewLine, _builders.Select(builder => builder.ToString()).ToArray());
        }

        void CreateInterface(Type type, Func<DynamicModelPropertyInfo, bool> act, Type baseInterface, bool isAbstract,
            Type rootBaseInterface, string namePrefix = null){
            var stringBuilder = new StringBuilder();
            var interfaceName = GetInterfaceName(_assemblyName, type, namePrefix);
            var classDecleration = ClassDecleration(type, baseInterface, isAbstract, rootBaseInterface, namePrefix);
            stringBuilder.AppendLine(classDecleration);
            var generatedInfos = new HashSet<string>();
            foreach (var propertyInfo in GetPropertyInfos(type, act)){
                if (!generatedInfos.Contains(propertyInfo.Name)){
                    var propertyCode = GetPropertyCode(propertyInfo, act, baseInterface);
                    stringBuilder.AppendLine(propertyCode);
                    generatedInfos.Add(propertyInfo.Name);
                }
            }
            stringBuilder.AppendLine("}");

            _createdInterfaces.Add(type, interfaceName);
            _builders.Add(stringBuilder);
        }

        string ClassDecleration(Type type, Type baseInterface, bool isAbstract, Type rootBaseInterface,
            string namePrefix){
            var classDecleration = string.Join(Environment.NewLine, AttributeLocator(type), ModelAbstract(isAbstract),
                ClassDeclarationCore(type, baseInterface, rootBaseInterface, namePrefix));
            return classDecleration;
        }

        string AttributeLocator(Type type){
            var classNameAttribute = new ClassNameAttribute(type.FullName);
            return $@"[{TypeToString(classNameAttribute.GetType())}(""{type.FullName}"")]";
        }

        [AttributeUsage(AttributeTargets.Interface)]
        public class ClassNameAttribute : Attribute{
            public ClassNameAttribute(string typeName){
                TypeName = typeName;
            }

            public string TypeName { get; }
        }

        string ModelAbstract(bool isAbstract){
            return isAbstract ? $"[{TypeToString(typeof(ModelAbstractClassAttribute))}()]" : null;
        }

        string ClassDeclarationCore(Type type, Type baseInterface, Type rootBaseInterface, string namePrefix){
            var interfaceName = GetInterfaceName(_assemblyName, type, namePrefix);
            var args = GetBaseInterface(baseInterface, rootBaseInterface, namePrefix == null);
            return $"public interface {interfaceName}:{args}{{";
        }

        string GetBaseInterface(Type baseInterface, Type rootBaseInterface, bool isRoot){
            return isRoot && rootBaseInterface != null ? TypeToString(rootBaseInterface) : TypeToString(baseInterface);
        }

        IEnumerable<DynamicModelPropertyInfo> GetPropertyInfos(Type type, Func<DynamicModelPropertyInfo, bool> act){
            var propertyInfos = type.GetValidProperties().Select(DynamicModelPropertyInfo);
            return act != null ? propertyInfos.Where(act) : propertyInfos;
        }

        DynamicModelPropertyInfo DynamicModelPropertyInfo(PropertyInfo info){
            return new DynamicModelPropertyInfo(info.Name, info.PropertyType, info.DeclaringType, info.CanRead,
                info.CanWrite, info);
        }

        static string GetInterfaceName(string assemblyName, Type type, string namePrefix = null){
            var interfaceName = string.Format("{0}" + assemblyName + "{1}", namePrefix ?? NamePrefix, type.Name);
            return interfaceName;
        }

        string GetPropertyCode(DynamicModelPropertyInfo property, Func<DynamicModelPropertyInfo, bool> filter,
            Type baseInterface){
            var setMethod = GetSetMethod(property);

            string interfaceName = null;
            if (setMethod == null){
                var reflectedType = property.ReflectedType;
                interfaceName = GetInterfaceName(_assemblyName, reflectedType);
                if (!_createdInterfaces.ContainsKey(property.PropertyType))
                    CreateInterface(property.PropertyType, filter, baseInterface, false, null, interfaceName);
            }

            var propertyCode =
                $"    {GetPropertyTypeCode(property, interfaceName)} {property.Name} {{ get; {setMethod} }}";
            return GetAttributesCode(property) + propertyCode;
        }


        string GetPropertyTypeCode(DynamicModelPropertyInfo property, string prefix = null){
            if (property.GetCustomAttributes(typeof(NullValueAttribute), false).Any()){
                return TypeToString(property.PropertyType);
            }
            if (!property.PropertyType.BehaveLikeValueType()){
                if (_createdInterfaces.ContainsKey(property.PropertyType))
                    return _createdInterfaces[property.PropertyType];
                return GetInterfaceName(_assemblyName, property.PropertyType, prefix);
            }
            Type propertyType = property.PropertyType;
            if (propertyType != typeof(string) && !propertyType.IsStruct())
                propertyType = typeof(Nullable<>).MakeNullAble(property.PropertyType);
            return TypeToString(propertyType);
        }

        object GetSetMethod(DynamicModelPropertyInfo property){
            return property.PropertyType.BehaveLikeValueType() ? "set;" : null;
        }

        string GetAttributesCode(DynamicModelPropertyInfo property){
            var attributes = property.GetCustomAttributes(false).OfType<Attribute>().ToList();
            if (property.PropertyType == typeof(string) && !attributes.OfType<LocalizableAttribute>().Any())
                attributes.Add(new LocalizableAttribute(true));
            IEnumerable<string> codeList =
                attributes.Select(attribute => GetAttributeCode(attribute, property))
                    .Where(attributeCode => !string.IsNullOrEmpty(attributeCode));
            return codeList.Aggregate<string, string>(null,
                (current, attributeCode) => current + $"   [{attributeCode}]{Environment.NewLine}");
        }

        string GetAttributeCode(object attribute, DynamicModelPropertyInfo info){
            if (attribute == null){
                return string.Empty;
            }
            var localizableAttribute = attribute as LocalizableAttribute;
            if (localizableAttribute != null && info.PropertyType == typeof(string)){
                string arg = (localizableAttribute).IsLocalizable.ToString(CultureInfo.InvariantCulture).ToLower();
                return string.Format("{1}({0})", arg, TypeToString(attribute.GetType()));
            }
            if (attribute.GetType() == typeof(CategoryAttribute)){
                string arg = ((CategoryAttribute) attribute).Category;
                return string.Format(@"{1}(""{0}"")", arg, TypeToString(attribute.GetType()));
            }
            if (attribute is RequiredAttribute){
                return $@"{TypeToString(attribute.GetType())}()";
            }
            var editorAttribute = attribute as EditorAttribute;
            if (editorAttribute != null){
                string arg1 = (editorAttribute).EditorTypeName;
                string arg2 = (editorAttribute).EditorBaseTypeName;
                return string.Format(@"{2}(""{0}"", ""{1}"")", arg1, arg2, TypeToString(attribute.GetType()));
            }
            var refreshPropertiesAttribute = attribute as RefreshPropertiesAttribute;
            if (refreshPropertiesAttribute != null){
                string arg = TypeToString(refreshPropertiesAttribute.RefreshProperties.GetType()) + "." +
                             refreshPropertiesAttribute.RefreshProperties.ToString();
                return string.Format(@"{1}({0})", arg, TypeToString(attribute.GetType()));
            }
            var typeConverterAttribute = attribute as TypeConverterAttribute;
            if (typeConverterAttribute != null){
                if (!(typeConverterAttribute).ConverterTypeName.Contains(".Design.") &&
                    !(typeConverterAttribute).ConverterTypeName.EndsWith(".Design")){
                    var type = Type.GetType((typeConverterAttribute).ConverterTypeName);
                    if (type != null && type.IsPublic && !type.FullName.Contains(".Design."))
                        return string.Format("{1}(typeof({0}))", type.FullName, TypeToString(attribute.GetType()));
                }
                return null;
            }
            Type attributeType = attribute.GetType();
            if (attributeType == typeof(DXDescriptionAttribute)){
                string description = ((DXDescriptionAttribute) attribute).Description.Replace(@"""", @"""""");
                return string.Format(@"{1}(@""{0}"")", description, TypeToString(typeof(DescriptionAttribute)));
            }
            if (typeof(DescriptionAttribute).IsAssignableFrom(attributeType)){
                string description = ((DescriptionAttribute) attribute).Description.Replace(@"""", @"""""");
                return string.Format(@"{1}(@""{0}"")", description, TypeToString(typeof(DescriptionAttribute)));
            }
            if (attributeType == typeof(DefaultValueAttribute)){
                string value = GetStringValue(((DefaultValueAttribute) attribute).Value);
                return $@"System.ComponentModel.DefaultValue({value})";
            }
            if (attributeType == typeof(ModelValueCalculatorWrapperAttribute)){
                var modelValueCalculatorAttribute = ((ModelValueCalculatorWrapperAttribute) attribute);
                if (modelValueCalculatorAttribute.CalculatorType != null){
                    return
                        $@"{TypeToString(typeof(ModelValueCalculatorAttribute))}(typeof({TypeToString(
                            modelValueCalculatorAttribute.CalculatorType)}))";
                }
                var linkValue = GetStringValue(modelValueCalculatorAttribute.LinkValue);
                return $@"{TypeToString(typeof(ModelValueCalculatorAttribute))}({linkValue})";
            }
            if (attributeType == typeof(ModelReadOnlyAttribute)){
                var readOnlyCalculatorType = ((ModelReadOnlyAttribute) attribute).ReadOnlyCalculatorType;
                return
                    $@"{TypeToString(typeof(ModelReadOnlyAttribute))}(typeof({TypeToString(readOnlyCalculatorType)}))";
            }
            if (attributeType == typeof(ReadOnlyAttribute)){
                string value = GetStringValue(((ReadOnlyAttribute) attribute).IsReadOnly);
                return string.Format(@"{1}({0})", value, TypeToString(attribute.GetType()));
            }
            return string.Empty;
        }

        string GetStringValue(object value){
            return GetStringValue(value?.GetType(), value);
        }

        string GetStringValue(Type type, object value){
            if (type == null || value == null){
                return "null";
            }
            Type nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null){
                type = nullableType;
            }
            if (type == typeof(string)){
                return $@"@""{((string) value).Replace("\"", "\"\"")}""";
            }
            if (type == typeof(Boolean)){
                return (bool) value ? "true" : "false";
            }
            if (type.IsEnum){
                if (value is int){
                    value = Enum.ToObject(type, (int) value);
                }
                return $"{TypeToString(type)}.{value}";
            }
            if (type.IsClass){
                return $"typeof({TypeToString(value.GetType())})";
            }
            if (type == typeof(char)){
                var args = Convert.ToString(value);
                if (args == "\0")
                    return @"""""";
                throw new NotImplementedException();
            }
            return string.Format(CultureInfo.InvariantCulture.NumberFormat, "({0})({1})", TypeToString(type), value);
        }

        string TypeToString(Type type){
            return HelperTypeGenerator.TypeToString(type, _usingTypes, true);
        }

        public void ExtendInteface(Type targetType, Type extenderType, Assembly assembly){
            extenderType = CalcType(extenderType, assembly);
            targetType = CalcType(targetType, assembly);
            _extenders.Add(targetType, extenderType);
        }

        public Type CalcType(Type extenderType, Assembly assembly){
            if (!extenderType.IsInterface){
                var type = assembly.GetTypes().SingleOrDefault(type1 => AttributeLocatorMatches(extenderType, type1));
                if (type == null){
                    throw new NullReferenceException("Cannot locate the dynamic interface for " + extenderType.FullName);
                }
                return type;
            }
            return extenderType;
        }

        bool AttributeLocatorMatches(Type extenderType, Type type1){
            return
                type1.GetCustomAttributes(typeof(Attribute), false)
                    .Any(attribute => AttributeMatch(extenderType, (Attribute) attribute));
        }

        bool AttributeMatch(Type extenderType, Attribute attribute){
            Type type = attribute.GetType();
            if (type.Name == typeof(ClassNameAttribute).Name){
                var value = attribute.GetPropertyValue("TypeName") + "";
                value = value.Substring(1 + value.LastIndexOf(".", StringComparison.Ordinal));
                return extenderType.Name == value;
            }
            return false;
        }

        public void ExtendInteface<TTargetInterface, TComponent>(Assembly assembly) where TComponent : class{
            ExtendInteface(typeof(TTargetInterface), typeof(TComponent), assembly);
        }

        public static string GeneratedEmptyInterfacesCode(IEnumerable<ITypeInfo> typeInfos, Type baseType,
            Func<ITypeInfo, Type, string, string> func = null){
            return
                typeInfos.Aggregate<ITypeInfo, string>(null,
                    (current, typeInfo) =>
                        current + (GenerateInterfaceCode(typeInfo, baseType, func) + Environment.NewLine))
                    .TrimEnd(Environment.NewLine.ToCharArray());
        }

        static string GenerateInterfaceCode(ITypeInfo typeInfo, Type baseType,
            Func<ITypeInfo, Type, string, string> func){
            var interfaceBuilder = new InterfaceBuilder();
            var classDeclaration = interfaceBuilder.ClassDeclarationCore(typeInfo.Type, baseType, null, null);
            string code = null;
            if (func != null)
                code = func.Invoke(typeInfo, baseType, GetInterfaceName(null, typeInfo.Type));

            var interfaceCode = code + Environment.NewLine + classDeclaration + Environment.NewLine + "}";
            return interfaceCode;
        }

        public static string GeneratedDisplayNameCode(string arg3){
            var interfaceBuilder = new InterfaceBuilder();
            return $@"[{interfaceBuilder.TypeToString(typeof(ModelDisplayNameAttribute))}(""{arg3}"")]";
        }
    }

    public class ModelValueCalculatorWrapperAttribute : Attribute{
        public ModelValueCalculatorWrapperAttribute(Type calculatorType){
            CalculatorType = calculatorType;
        }

        public ModelValueCalculatorWrapperAttribute(ModelValueCalculatorAttribute modelValueCalculatorAttribute,
            Type calculatorType)
            : this(calculatorType){
            LinkValue = modelValueCalculatorAttribute.LinkValue;
            NodeName = modelValueCalculatorAttribute.NodeName;
            if (modelValueCalculatorAttribute.NodeType != null)
                NodeTypeName = modelValueCalculatorAttribute.NodeType.Name;
            PropertyName = modelValueCalculatorAttribute.PropertyName;
        }

        public Type CalculatorType { get; }

        public string LinkValue { get; }

        public string NodeName { get; }

        public string NodeTypeName { get; }

        public string PropertyName { get; }
    }

    public static class InterfaceBuilderExtensions{
        public static Type MakeNullAble(this Type generic, params Type[] args){
            return args[0].IsNullableType() ? args[0] : typeof(Nullable<>).MakeGenericType(args);
        }


        public static bool FilterAttributes(this DynamicModelPropertyInfo info, Type[] attributes){
            return attributes.SelectMany(type => info.GetCustomAttributes(type, false)).Any();
        }

        public static bool DXFilter(this DynamicModelPropertyInfo info, Type[] attributes = null){
            return info.DXFilter(info.DeclaringType, attributes);
        }

        public static readonly IList<Type> BaseTypes = new List<Type>{
            typeof(BaseOptions),
            typeof(FormatInfo),
            typeof(AppearanceObject),
            typeof(TextOptions),
            typeof(BaseAppearanceCollection),
            typeof(PropertiesBase),
            typeof(WebControl)
        };

        public static readonly HashSet<string> ExcludedReservedNames = new HashSet<string>{"IsReadOnly"};

        public static bool DXFilter(this DynamicModelPropertyInfo info, Type componentBaseType, Type[] attributes = null){
            return DXFilter(info, BaseTypes, componentBaseType, attributes);
        }

        public static bool DXFilter(this DynamicModelPropertyInfo info, IList<Type> baseTypes, Type componentBaseType,
            Type[] attributes = null){
            if (attributes == null)
                attributes = new[]{typeof(XtraSerializableProperty)};
            return Filter(info, componentBaseType, BaseTypes.Union(baseTypes).ToArray(), attributes);
        }

        public static bool Filter(this DynamicModelPropertyInfo info, Type componentBaseType,
            Type[] filteredPropertyBaseTypes, Type[] attributes){
            return info.IsBrowseable() && info.HasAttributes(attributes) && !ExcludedReservedNames.Contains(info.Name) &&
                   FilterCore(info, componentBaseType, filteredPropertyBaseTypes);
        }

        static bool FilterCore(DynamicModelPropertyInfo info, Type componentBaseType,
            IEnumerable<Type> filteredPropertyBaseTypes){
            var behaveLikeValueType = info.PropertyType.BehaveLikeValueType();
            var isBaseViewProperty = componentBaseType.IsAssignableFrom(info.DeclaringType);
            var propertyBaseTypes = filteredPropertyBaseTypes as Type[] ?? filteredPropertyBaseTypes.ToArray();
            var filterCore = propertyBaseTypes.Any(type => type.IsAssignableFrom(info.PropertyType)) ||
                             behaveLikeValueType;
            var core = propertyBaseTypes.Any(type => type.IsAssignableFrom(info.DeclaringType)) && behaveLikeValueType;
            return isBaseViewProperty ? filterCore : core;
        }

        public static bool IsValidEnum(this Type propertyType, object value){
            return !propertyType.IsEnum || Enum.IsDefined(value.GetType(), value);
        }

        public static bool IsStruct(this Type type){
            if (type.IsNullableType())
                type = type.GetGenericArguments()[0];
            return type.IsValueType && !type.IsEnum && !type.IsPrimitive;
        }

        public static bool IsBrowseable(this PropertyInfo propertyInfo){
            return
                propertyInfo.GetCustomAttributes(typeof(BrowsableAttribute), false)
                    .OfType<BrowsableAttribute>()
                    .All(o => o.Browsable);
        }

        public static bool BehaveLikeValueType(this Type type){
            return type == typeof(string) || type.IsValueType;
        }

        public static IEnumerable<PropertyInfo> GetValidProperties(this Type type, params Type[] attributes){
            if (type != null){
                var propertyInfos =
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                        .Distinct(new PropertyInfoEqualityComparer());
                var infos = propertyInfos.Where(info => HasAttributes(info, attributes)).ToArray();
                if (infos.Any(info => info.Name.StartsWith("TextFormatSt"))){
                    var array = infos.Where(IsValidProperty).ToArray();
                    Debug.Print(array.Length.ToString());
                }
                return infos.Where(IsValidProperty).ToArray();
            }
            return new PropertyInfo[0];
        }

        static bool HasAttributes(this PropertyInfo propertyInfo, params Type[] attributes){
            var hasAttributes = (attributes == null || attributes == Type.EmptyTypes) || (attributes.Any()) ||
                                (attributes.All(type => propertyInfo.GetCustomAttributes(type, false).Any()));
            return hasAttributes;
        }

        class PropertyInfoEqualityComparer : IEqualityComparer<PropertyInfo>{
            public bool Equals(PropertyInfo x, PropertyInfo y){
                return x.Name.Equals(y.Name);
            }

            public int GetHashCode(PropertyInfo obj){
                return obj.Name.GetHashCode();
            }
        }

        static bool IsValidProperty(PropertyInfo info){
            if (IsObsolete(info))
                return false;
            var isNotEnumerable = !typeof(IEnumerable).IsAssignableFrom(info.PropertyType)||typeof(string)==info.PropertyType;
            return (!info.PropertyType.BehaveLikeValueType() ||
                   info.GetSetMethod() != null && info.GetGetMethod() != null)&&(isNotEnumerable);
        }

        static bool IsObsolete(PropertyInfo info){
            return info.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0;
        }

        public static void SetBrowsable(this DynamicModelPropertyInfo info, Dictionary<string, bool> propertyNames){
            if (propertyNames.ContainsKey(info.Name)){
                info.RemoveAttributes(typeof(BrowsableAttribute));
                info.AddAttribute(new BrowsableAttribute(propertyNames[info.Name]));
            }
        }

        public static void SetCategory(this DynamicModelPropertyInfo info, Dictionary<string, string> propertyNames){
            if (propertyNames.ContainsKey(info.Name)){
                info.RemoveAttributes(typeof(BrowsableAttribute));
                info.AddAttribute(new CategoryAttribute(propertyNames[info.Name]));
            }
        }

        public static void CreateValueCalculator(this DynamicModelPropertyInfo info,
            IModelValueCalculator modelValueCalculator){
            CreateValueCalculatorCore(info);
            info.AddAttribute(new ModelValueCalculatorWrapperAttribute(modelValueCalculator.GetType()));
        }

        public static void CreateValueCalculator(this DynamicModelPropertyInfo info, string expressionPath = null){
            info.AddAttribute(new BrowsableAttribute(false));
        }

        static void CreateValueCalculatorCore(DynamicModelPropertyInfo info){
            info.RemoveAttributes(typeof(DefaultValueAttribute));
            info.AddAttribute(new ReadOnlyAttribute(true));
        }

        public static void SetDefaultValues(this DynamicModelPropertyInfo info, Dictionary<string, object> propertyNames){
            if (propertyNames.ContainsKey(info.Name)){
                info.RemoveAttributes(typeof(DefaultValueAttribute));
                info.AddAttribute(new DefaultValueAttribute(propertyNames[info.Name]));
            }
        }
    }

    public sealed class DynamicModelPropertyInfo : PropertyInfo{
        readonly List<object> _attributesCore = new List<object>();
        readonly PropertyInfo _targetPropertyInfo;
        private Type _propertyType;
        private string _name;

        public DynamicModelPropertyInfo(string name, Type propertyType, Type declaringType, bool canRead, bool canWrite,
            PropertyInfo targetPropertyInfo){
            _name = name;
            _propertyType = propertyType;
            DeclaringType = declaringType;
            CanRead = canRead;
            CanWrite = canWrite;
            _targetPropertyInfo = targetPropertyInfo;
            var collection = targetPropertyInfo.GetCustomAttributes(false).Where(o => !(o is DefaultValueAttribute));
            _attributesCore.AddRange(collection);
        }


        public override string Name => _name;

        public override Type PropertyType => _propertyType;

        public override Type DeclaringType { get; }

        public override bool CanRead { get; }

        public override bool CanWrite { get; }

        public override PropertyAttributes Attributes{
            get { throw new NotImplementedException(); }
        }

        public override Type ReflectedType => _targetPropertyInfo.ReflectedType;

        public override string ToString(){
            return _targetPropertyInfo.ToString();
        }

        public override MethodInfo[] GetAccessors(bool nonPublic){
            return _targetPropertyInfo.GetAccessors(nonPublic);
        }

        public override MethodInfo GetGetMethod(bool nonPublic){
            return _targetPropertyInfo.GetSetMethod(nonPublic);
        }

        public override ParameterInfo[] GetIndexParameters(){
            return _targetPropertyInfo.GetIndexParameters();
        }

        public override MethodInfo GetSetMethod(bool nonPublic){
            return _targetPropertyInfo.GetSetMethod(nonPublic);
        }

        public override object GetValue(object obj, BindingFlags invokeAttr, Binder binder, object[] index,
            CultureInfo culture){
            return _targetPropertyInfo.GetValue(obj, invokeAttr, binder, index, culture);
        }

        public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, object[] index,
            CultureInfo culture){
            _targetPropertyInfo.SetValue(obj, value, invokeAttr, binder, index, culture);
        }

        public override object[] GetCustomAttributes(Type attributeType, bool inherit){
            return _attributesCore.Where(attributeType.IsInstanceOfType).ToArray();
        }

        public override object[] GetCustomAttributes(bool inherit){
            return _attributesCore.ToArray();
        }

        public override bool IsDefined(Type attributeType, bool inherit){
            return _targetPropertyInfo.IsDefined(attributeType, inherit);
        }


        public void SetName(string name) {
            _name = name;
        }

        public void SetPropertyType(Type type) {
            _propertyType = type;
        }

        public void RemoveAttribute(Attribute attribute){
            _attributesCore.Remove(attribute);
        }

        public void AddAttribute(Attribute attribute){
            _attributesCore.Add(attribute);
        }

        public void RemoveInvalidTypeConverterAttributes(string nameSpace){
            var customAttributes =
                GetCustomAttributes(typeof(TypeConverterAttribute), false).OfType<TypeConverterAttribute>();
            var attributes = customAttributes.Where(attribute => attribute.ConverterTypeName.StartsWith(nameSpace));
            foreach (var customAttribute in attributes){
                _attributesCore.Remove(customAttribute);
            }
        }

        public void RemoveAttributes(Type type){
            foreach (var customAttribute in GetCustomAttributes(type, false).OfType<Attribute>()){
                _attributesCore.Remove(customAttribute);
            }
        }
    }
}