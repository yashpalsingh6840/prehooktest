<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <ProjectTypeGuids>{30D016F9-3734-4E33-A861-5E7D899E18F3};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProductVersion>8.0.30703</ProductVersion>
    <SchemaVersion>2.0</SchemaVersion>
    <ProjectGuid>{06298390-5123-5FB6-B7CC-5BF12C11CD1A}</ProjectGuid>
    <OutputType>Library</OutputType>
    <AppDesignerFolder>Properties</AppDesignerFolder>
    <RootNamespace>SC_ComputeCustomerTier_gen</RootNamespace>
    <AssemblyName>SC_ComputeCustomerTier_gen</AssemblyName>
    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
    <FileAlignment>512</FileAlignment>
    <ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch>true</ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Debug' ">
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>.\bin\Debug\</OutputPath>
    <EnableUnmanagedDebugging>false</EnableUnmanagedDebugging>
    <DefineConstants>DEBUG;TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Release' ">
    <DebugSymbols>false</DebugSymbols>
    <Optimize>true</Optimize>
    <OutputPath>.\bin\Release\</OutputPath>
    <EnableUnmanagedDebugging>false</EnableUnmanagedDebugging>
    <DefineConstants>TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Data" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.Xml" />
    <Reference Include="Microsoft.SqlServer.TxScript, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91" />
    <Reference Include="Microsoft.SqlServer.DTSRuntimeWrap, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91" />
    <Reference Include="Microsoft.SqlServer.DTSPipelineWrap, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91" />
    <Reference Include="Microsoft.SqlServer.PipelineHost, Version=17.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91" />
    <Compile Include="main.cs" />
    <Compile Include="BufferWrapper.cs" />
    <Compile Include="ComponentWrapper.cs" />
  </ItemGroup>
  <ItemGroup>
    <AppDesigner Include="Properties\" />
    <Compile Include="Properties\AssemblyInfo.cs">
      <SubType>Code</SubType>
    </Compile>
    <EmbeddedResource Include="Properties\Resources.resx">
      <Generator>ResXFileCodeGenerator</Generator>
      <LastGenOutput>Resources.Designer.cs</LastGenOutput>
    </EmbeddedResource>
    <Compile Include="Properties\Resources.Designer.cs">
      <AutoGen>True</AutoGen>
      <DependentUpon>Resources.resx</DependentUpon>
    </Compile>
    <None Include="Properties\Settings.settings">
      <Generator>SettingsSingleFileGenerator</Generator>
      <LastGenOutput>Settings.Designer.cs</LastGenOutput>
    </None>
    <Compile Include="Properties\Settings.Designer.cs">
      <AutoGen>True</AutoGen>
      <DependentUpon>Settings.settings</DependentUpon>
    </Compile>
  </ItemGroup>
  <!-- Include the build rules for a C# project.-->
  <Import Project="$(MSBuildBinPath)\Microsoft.CSharp.targets" />
  <!-- This section defines VSTA properties that describe the host-changable project properties. -->
  <ProjectExtensions>
    <VisualStudio>
      <FlavorProperties GUID="{30D016F9-3734-4E33-A861-5E7D899E18F3}">
        <ProjectProperties HostName="ScriptComponent" HostPackage="{B3A685AA-7EAF-4BC6-9940-57959FA5AC07}" ApplicationType="usd" Language="cs" TemplatesPath="" DebugInfoExeName="" DebugInfoCommandLine="" DebugInfoWorkingDir="" IconImageList="" />
        <Host Name="ScriptComponent" GeneratedNamespace="SC_ComputeCustomerTier_gen" IconIndex="0"></Host>
        <ProjectClient>
          <HostIdentifier>SSIS_SC170</HostIdentifier>
        </ProjectClient>
      </FlavorProperties>
    </VisualStudio>
  </ProjectExtensions>
</Project>