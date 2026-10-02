namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The NativeOnly reason texts and stable machine codes <see cref="NifModelAnimationReader" /> records (cut-1b slices 4,
///     6, 7 and 14; plan section 2.1, owner rulings D6, D7 and D11, RE-21, RE-22 and RE-23). A curve, state or clock refusal of
///     the slice-2 mappings is described by <see cref="Reason(NifModelCurveBlock)" /> and <see cref="Code(NifModelCurveBlock)" />;
///     a target refusal of slice 3 keeps <see cref="NifModelTargetNames.Reason" /> and <see cref="NifModelTargetNames.Code" />.
/// </summary>
/// <remarks>
///     Since slices 13 and 16b the Euler and quaternion TBC and QUADRATIC rotations map (Shared <c>68335d3</c> ships their
///     forms), so the former 'not mapped yet' rows are gone; what remains for those kinds are the engine-behavior guards
///     of RE-20 rules 2 and 7 and RE-24, with reasons of their own.
/// </remarks>
internal static class NifModelAnimationReasons
{
    /// <summary>The code of a Typed decision.</summary>
    public const string TypedCode = "typed";

    /// <summary>
    ///     A guard that should not fire after RE-21's collapse: two tracks of one sequence would still drive one (node,
    ///     property), which Shared rejects, so the whole sequence stays native.
    /// </summary>
    public const string RepeatedTarget = "repeated transform target in sequence (D7; RE-21)";

    /// <summary>The code of <see cref="RepeatedTarget" />.</summary>
    public const string RepeatedTargetCode = "repeatedTarget";

    /// <summary>RE-21 step 5: repeated transform targets whose content differs; the engine blends them.</summary>
    public const string RepeatedDifferingContent =
        "repeated transform target with differing content: the engine blends them (RE-21)";

    /// <summary>The code of <see cref="RepeatedDifferingContent" />.</summary>
    public const string RepeatedDifferingContentCode = "repeatedDifferingContent";

    /// <summary>
    ///     RE-21 step 2: repeated transform targets with differing priorities. The engine eases the lower ones out, but the
    ///     activation priority (0xFF) makes the comparison uncertain and retail has no case, so nothing is dropped.
    /// </summary>
    public const string RepeatedDifferingPriority =
        "repeated transform target with differing priorities: not collapsed (RE-21; no retail case)";

    /// <summary>The code of <see cref="RepeatedDifferingPriority" />.</summary>
    public const string RepeatedDifferingPriorityCode = "repeatedDifferingPriority";

    /// <summary>RE-21 step 4: a repeated controlled block with identical content collapsed onto the lowest index.</summary>
    public const string RepeatedCollapsed = "repeated controlled block collapsed: identical content (RE-21)";

    /// <summary>The code of <see cref="RepeatedCollapsed" />.</summary>
    public const string RepeatedCollapsedCode = "repeatedCollapsed";

    /// <summary>
    ///     The collapse reason with the kept controlled block and the multiplicity k, which RE-21 keeps as native state (the
    ///     engine's effective weight on that node is k times the sequence weight).
    /// </summary>
    /// <param name="kept">The controlled-block index whose track is emitted.</param>
    /// <param name="multiplicity">How many controlled blocks named the target.</param>
    /// <returns>The reason text.</returns>
    public static string Collapsed(int kept, int multiplicity)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{RepeatedCollapsed}; duplicate of controlled block {kept}; multiplicity {multiplicity}");
    }

    /// <summary>Two free-running controllers would drive one (node, property) in the <c>(controllers)</c> clip.</summary>
    public const string RepeatedEmbeddedTarget = "repeated transform target among free-running controllers";

    /// <summary>The code of <see cref="RepeatedEmbeddedTarget" />.</summary>
    public const string RepeatedEmbeddedTargetCode = "repeatedEmbeddedTarget";

    /// <summary>
    ///     D11: the sequence fields with no Shared field. Weight, accumulation root and priority are typed as source
    ///     policies too; every one of these fields is also copied into the clip's extras.
    /// </summary>
    public const string SequenceNativeFields =
        "sequence fields with no Shared field (manager ref, text-key ref, controlled-block property type, controller " +
        "type, controller ID and interpolator ID strings) are kept in the clip extras (D11)";

    /// <summary>The code of <see cref="SequenceNativeFields" />.</summary>
    public const string SequenceNativeFieldsCode = "sequenceNativeFields";

    /// <summary>The sequence block is not a readable 20.2.0.7 NiControllerSequence.</summary>
    public const string SequenceUnreadable = "sequence did not read as a 20.2.0.7 NiControllerSequence";

    /// <summary>The code of <see cref="SequenceUnreadable" />.</summary>
    public const string SequenceUnreadableCode = "sequenceUnreadable";

    /// <summary>The sequence's fields do not end exactly where the block ends, so the layout read is not the stored one.</summary>
    public const string SequenceNotExact = "sequence fields not consumed exactly";

    /// <summary>The code of <see cref="SequenceNotExact" />.</summary>
    public const string SequenceNotExactCode = "sequenceNotExact";

    /// <summary>The sequence's Name index lies outside the header string table.</summary>
    public const string SequenceNameUnresolved = "sequence name index outside the string table";

    /// <summary>The code of <see cref="SequenceNameUnresolved" />.</summary>
    public const string SequenceNameUnresolvedCode = "sequenceNameUnresolved";

    /// <summary>The sequence's Weight is not finite, or its Accum Root Name index lies outside the string table.</summary>
    public const string SequencePolicyUnreadable = "sequence weight or accumulation root name unreadable";

    /// <summary>The code of <see cref="SequencePolicyUnreadable" />.</summary>
    public const string SequencePolicyUnreadableCode = "sequencePolicyUnreadable";

    /// <summary>
    ///     A controlled block whose Controller Type is none the reader binds: not NiTransformController,
    ///     NiGeomMorpherController, nor one of the five property and visibility controllers of slice 14.
    /// </summary>
    public const string NotTransformBlock =
        "controlled block is not a transform, morph, property or visibility controller: no track";

    /// <summary>The code of <see cref="NotTransformBlock" />.</summary>
    public const string NotTransformBlockCode = "notTransformBlock";

    /// <summary>A controlled block's Controller Type index lies outside the string table.</summary>
    public const string ControllerTypeUnresolved = "controlled block controller type index outside the string table";

    /// <summary>The code of <see cref="ControllerTypeUnresolved" />.</summary>
    public const string ControllerTypeUnresolvedCode = "controllerTypeUnresolved";

    /// <summary>A transform controlled block (or free-running controller) whose Interpolator ref names no block.</summary>
    public const string NoInterpolator = "no interpolator: no curve";

    /// <summary>The code of <see cref="NoInterpolator" />.</summary>
    public const string NoInterpolatorCode = "noInterpolator";

    /// <summary>The sequence's text keys when the sequence becomes no clip (plan section 2.1).</summary>
    public const string TextKeysOutsideClip = "text keys outside a clip";

    /// <summary>The code of <see cref="TextKeysOutsideClip" />.</summary>
    public const string TextKeysOutsideClipCode = "textKeysOutsideClip";

    /// <summary>The sequence's Text Keys ref names a block that is not a readable NiTextKeyExtraData.</summary>
    public const string TextKeysUnreadable = "text keys did not read as an NiTextKeyExtraData";

    /// <summary>The code of <see cref="TextKeysUnreadable" />.</summary>
    public const string TextKeysUnreadableCode = "textKeysUnreadable";

    /// <summary>An NiControllerManager none of whose sequences became a clip (plan section 2.1).</summary>
    public const string ManagerNoClip = "manager: no clip";

    /// <summary>The code of <see cref="ManagerNoClip" />.</summary>
    public const string ManagerNoClipCode = "managerNoClip";

    /// <summary>An NiControllerManager whose Controller Sequences list did not decode.</summary>
    public const string SequenceListUnreadable = "manager sequence list did not decode";

    /// <summary>The code of <see cref="SequenceListUnreadable" />.</summary>
    public const string SequenceListUnreadableCode = "sequenceListUnreadable";

    /// <summary>A manager sequence-list entry that is not an NiControllerSequence block.</summary>
    public const string SequenceListEntry = "manager sequence list entry is not an NiControllerSequence";

    /// <summary>The code of <see cref="SequenceListEntry" />.</summary>
    public const string SequenceListEntryCode = "sequenceListEntry";

    /// <summary>A controller block too short for the NiTimeController header.</summary>
    public const string ControllerUnreadable = "controller header did not read";

    /// <summary>The code of <see cref="ControllerUnreadable" />.</summary>
    public const string ControllerUnreadableCode = "controllerUnreadable";

    /// <summary>
    ///     RE-22 rule 1 and D6: a manager-controlled (sequence-driven) controller binds only through its sequences; its own
    ///     clock and active bit are never evaluated.
    /// </summary>
    public const string ManagerControlled = "manager-controlled controller: plays only through its sequences (RE-22)";

    /// <summary>The code of <see cref="ManagerControlled" />.</summary>
    public const string ManagerControlledCode = "managerControlled";

    /// <summary>A free-running controller whose Target is not a placed document node.</summary>
    public const string TargetNotPlaced = "controller target is not a placed node";

    /// <summary>The code of <see cref="TargetNotPlaced" />.</summary>
    public const string TargetNotPlacedCode = "controllerTargetNotPlaced";

    /// <summary>RE-22 rule 3a: the double sentinel clock on a controller with no curve: no clock and no track.</summary>
    public const string SentinelWithoutCurve = "sentinel clock without a curve: no track (RE-22)";

    /// <summary>The code of <see cref="SentinelWithoutCurve" />.</summary>
    public const string SentinelWithoutCurveCode = "sentinelWithoutCurve";

    /// <summary>Manager-side NiMultiTargetTransformController state (plan sections 1.6 and 2.1).</summary>
    public const string MultiTargetBinding = "manager binding: no curve";

    /// <summary>The code of <see cref="MultiTargetBinding" />.</summary>
    public const string MultiTargetBindingCode = "managerBinding";

    /// <summary>Manager-side NiBlend*Interpolator state (plan sections 1.6 and 2.1).</summary>
    public const string BlendState = "manager blend state (runtime)";

    /// <summary>The code of <see cref="BlendState" />.</summary>
    public const string BlendStateCode = "blendState";

    /// <summary>BSRotAccumTransfInterpolator (plan section 2.1).</summary>
    public const string RotationAccumulation = "rotation-accumulation semantics not established";

    /// <summary>The code of <see cref="RotationAccumulation" />.</summary>
    public const string RotationAccumulationCode = "rotationAccumulation";

    /// <summary>BSTreadTransfInterpolator (plan section 2.1).</summary>
    public const string TreadTransform = "tread transform: no Shared form";

    /// <summary>The code of <see cref="TreadTransform" />.</summary>
    public const string TreadTransformCode = "treadTransform";

    /// <summary>NiPathInterpolator and NiLookAtInterpolator (plan section 2.1).</summary>
    public const string PathLookAt = "Path/LookAt: no Shared constraint";

    /// <summary>The code of <see cref="PathLookAt" />.</summary>
    public const string PathLookAtCode = "pathLookAt";

    /// <summary>An interpolator under a transform binding whose type is not a transform interpolator.</summary>
    public const string NotTransformInterpolator = "interpolator is not a transform interpolator";

    /// <summary>The code of <see cref="NotTransformInterpolator" />.</summary>
    public const string NotTransformInterpolatorCode = "notTransformInterpolator";

    /// <summary>A transform interpolator block that does not read as its 20.2.0.7 layout.</summary>
    public const string InterpolatorUnreadable = "transform interpolator did not read";

    /// <summary>The code of <see cref="InterpolatorUnreadable" />.</summary>
    public const string InterpolatorUnreadableCode = "interpolatorUnreadable";

    /// <summary>An interpolator data ref that names no block, or a block of the wrong type.</summary>
    public const string DataUnresolved = "interpolator data ref names no key-data block";

    /// <summary>The code of <see cref="DataUnresolved" />.</summary>
    public const string DataUnresolvedCode = "dataUnresolved";

    /// <summary>Key data (NiTransformData, NiKeyframeData, NiBSplineData, NiBSplineBasisData, NiFloatData) that did not read exactly.</summary>
    public const string DataUnreadable = "key data did not read exactly";

    /// <summary>The code of <see cref="DataUnreadable" />.</summary>
    public const string DataUnreadableCode = "dataUnreadable";

    /// <summary>
    ///     RE-23 rule 3: morph 0's (Base) interpolator, keyed, static, blend or null, has no effect under relative targets;
    ///     the engine forces its weight to 1.0 and never calls its interpolator. Nothing is emitted for slot 0.
    /// </summary>
    public const string BaseWeight = "Base weight: no effect under relative targets (RE-23, engine: forced 1.0)";

    /// <summary>The code of <see cref="BaseWeight" />.</summary>
    public const string BaseWeightCode = "baseWeight";

    /// <summary>
    ///     RE-21 step 7: two controlled blocks of one sequence bind the same morph target; the engine averages them through
    ///     one blend interpolator, so neither is picked and both stay native.
    /// </summary>
    public const string RepeatedMorphTarget =
        "repeated morph target in sequence: the engine averages the pair, kept native (RE-21 step 7)";

    /// <summary>The code of <see cref="RepeatedMorphTarget" />.</summary>
    public const string RepeatedMorphTargetCode = "repeatedMorphTarget";

    /// <summary>
    ///     RE-23 rules 1 and 7: the target's NiMorphData Relative Targets byte is not 1, so the engine sums every slot
    ///     including the Base's own weight; typing that needs w0 = 1 - sum(w) at every key, which is not established.
    /// </summary>
    public const string AbsoluteMorphTargets = "absolute morph targets with a free base weight (RE-23 rule 7)";

    /// <summary>The code of <see cref="AbsoluteMorphTargets" />.</summary>
    public const string AbsoluteMorphTargetsCode = "absoluteMorphTargets";

    /// <summary>A morph beyond the Base whose controller stores no Interpolator Weights item for it (RE-23 rule 2 has no weight).</summary>
    public const string MorphSlotNoItem = "morph slot without an interpolator item: weight not established";

    /// <summary>The code of <see cref="MorphSlotNoItem" />.</summary>
    public const string MorphSlotNoItemCode = "morphSlotNoItem";

    /// <summary>A morph weight with neither keys nor a value (a sentinel static and no stored item weight).</summary>
    public const string MorphNoValue = "morph weight has no value: sentinel static and no keys";

    /// <summary>The code of <see cref="MorphNoValue" />.</summary>
    public const string MorphNoValueCode = "morphNoValue";

    /// <summary>An interpolator under a morph binding whose type drives no float (not NiFloatInterpolator or a float B-spline).</summary>
    public const string NotFloatInterpolator = "interpolator is not a float interpolator";

    /// <summary>The code of <see cref="NotFloatInterpolator" />.</summary>
    public const string NotFloatInterpolatorCode = "notFloatInterpolator";

    /// <summary>An NiGeomMorpherController that does not read as its 20.2.0.7 layout, exactly.</summary>
    public const string MorpherUnreadable = "morpher controller did not read exactly";

    /// <summary>The code of <see cref="MorpherUnreadable" />.</summary>
    public const string MorpherUnreadableCode = "morpherUnreadable";

    /// <summary>A morpher's Data ref that names no NiMorphData block.</summary>
    public const string MorphDataUnresolved = "morpher data ref names no NiMorphData";

    /// <summary>The code of <see cref="MorphDataUnresolved" />.</summary>
    public const string MorphDataUnresolvedCode = "morphDataUnresolved";

    /// <summary>An NiMorphData whose frame table does not read exactly.</summary>
    public const string MorphDataUnreadable = "morph data did not read exactly";

    /// <summary>The code of <see cref="MorphDataUnreadable" />.</summary>
    public const string MorphDataUnreadableCode = "morphDataUnreadable";

    /// <summary>
    ///     Cut-1b slice 10: a morph binding whose target occurrence draws no mesh carrying exactly the morph data's targets
    ///     (the cut-1a geometry and morph readers did not type them, for example a vertex count that differs from the
    ///     geometry's, a packed layout outside the six measured ones, or geometry that yields no primitive). Shared
    ///     requires a morph channel to match its node's mesh target domain, so no weight track is emitted.
    /// </summary>
    public const string MorphTargetsNotTyped =
        "morph targets not typed on the target's mesh: no weight channel can address them";

    /// <summary>The code of <see cref="MorphTargetsNotTyped" />.</summary>
    public const string MorphTargetsNotTypedCode = "morphTargetsNotTyped";

    /// <summary>
    ///     A morpher that is not the first NiGeomMorpherController on its target's controller chain: the cut-1a geometry
    ///     reader types only the first one's morph data, so this one's targets do not exist in the mesh.
    /// </summary>
    public const string ExtraMorpher =
        "second morpher controller on the same geometry: its morph data is not typed (cut 1a)";

    /// <summary>The code of <see cref="ExtraMorpher" />.</summary>
    public const string ExtraMorpherCode = "extraMorpher";

    /// <summary>A morph controlled block whose Interpolator ID is the NULL string or lies outside the string table.</summary>
    public const string MorphFrameUnresolved = "interpolator ID names no frame (NULL or outside the string table)";

    /// <summary>The code of <see cref="MorphFrameUnresolved" />.</summary>
    public const string MorphFrameUnresolvedCode = "morphFrameUnresolved";

    /// <summary>A morph controlled block whose Interpolator ID matches no Frame Name of the target's morph data (exact bytes).</summary>
    public const string MorphFrameNotFound = "interpolator ID matches no Frame Name of the target's morph data";

    /// <summary>The code of <see cref="MorphFrameNotFound" />.</summary>
    public const string MorphFrameNotFoundCode = "morphFrameNotFound";

    /// <summary>A morph controlled block whose target node carries no NiGeomMorpherController on its controller chain.</summary>
    public const string MorphTargetNoMorpher = "target has no NiGeomMorpherController on its controller chain";

    /// <summary>The code of <see cref="MorphTargetNoMorpher" />.</summary>
    public const string MorphTargetNoMorpherCode = "morphTargetNoMorpher";

    /// <summary>An interpolator under a color binding whose type drives no Point3 (not NiPoint3Interpolator or a Point3 B-spline).</summary>
    public const string NotPoint3Interpolator = "interpolator is not a Point3 interpolator";

    /// <summary>The code of <see cref="NotPoint3Interpolator" />.</summary>
    public const string NotPoint3InterpolatorCode = "notPoint3Interpolator";

    /// <summary>An interpolator under a visibility binding whose type drives no bool (not NiBoolInterpolator or NiBoolTimelineInterpolator).</summary>
    public const string NotBoolInterpolator = "interpolator is not a bool interpolator";

    /// <summary>The code of <see cref="NotBoolInterpolator" />.</summary>
    public const string NotBoolInterpolatorCode = "notBoolInterpolator";

    /// <summary>A Point3 pose value or B-spline static that mixes the -FLT_MAX sentinel with authored components (a shape nif.xml does not define).</summary>
    public const string StaticMixesSentinel = "static value mixes the sentinel with authored components";

    /// <summary>The code of <see cref="StaticMixesSentinel" />.</summary>
    public const string StaticMixesSentinelCode = "staticMixesSentinel";

    /// <summary>A property interpolator with neither keys nor a pose value (the sentinel static and no data).</summary>
    public const string PropertyNoValue = "property interpolator has no value: sentinel static and no keys";

    /// <summary>The code of <see cref="PropertyNoValue" />.</summary>
    public const string PropertyNoValueCode = "propertyNoValue";

    /// <summary>A visibility key or pose value that is not exactly 0 or 1 (Shared refuses it; nothing is clamped).</summary>
    public const string VisibilityNotBinary = "visibility value not exactly 0 or 1";

    /// <summary>The code of <see cref="VisibilityNotBinary" />.</summary>
    public const string VisibilityNotBinaryCode = "visibilityNotBinary";

    /// <summary>
    ///     An NiBoolData group whose key type is not CONST (5): retail stores nothing else, and the engine's evaluation of
    ///     a LINEAR, QUADRATIC or TBC bool key is not established, so it fails closed.
    /// </summary>
    public const string VisibilityKeyType = "bool key type other than CONST: the engine's bool key evaluation is not established";

    /// <summary>The code of <see cref="VisibilityKeyType" />.</summary>
    public const string VisibilityKeyTypeCode = "visibilityKeyType";

    /// <summary>
    ///     A property or visibility controlled block of a <c>.kf</c>: the skeleton is read as nodes only (D12), so the
    ///     property blocks and the controller chains the engine binds through are not available to the read.
    /// </summary>
    public const string SkeletonPropertyBinding =
        "property or visibility binding in a .kf: the skeleton's property blocks and controller chains are not read " +
        "(nodes only, D12), so the binding cannot be verified";

    /// <summary>The code of <see cref="SkeletonPropertyBinding" />.</summary>
    public const string SkeletonPropertyBindingCode = "skeletonPropertyBinding";

    /// <summary>A property controller block whose own fields (Target Color, Texture Slot, Operation, Texture Set) did not read.</summary>
    public const string ControllerFieldsUnreadable = "property controller fields did not read";

    /// <summary>The code of <see cref="ControllerFieldsUnreadable" />.</summary>
    public const string ControllerFieldsUnreadableCode = "controllerFieldsUnreadable";

    /// <summary>A controlled block whose Property Type index lies outside the string table.</summary>
    public const string PropertyTypeUnresolved = "controlled block property type index outside the string table";

    /// <summary>The code of <see cref="PropertyTypeUnresolved" />.</summary>
    public const string PropertyTypeUnresolvedCode = "propertyTypeUnresolved";

    /// <summary>A controlled block whose Controller ID index lies outside the string table.</summary>
    public const string ControllerIdUnresolved = "controlled block controller ID index outside the string table";

    /// <summary>The code of <see cref="ControllerIdUnresolved" />.</summary>
    public const string ControllerIdUnresolvedCode = "controllerIdUnresolved";

    /// <summary>An NiVisController controlled block that names a property type: the engine binds visibility on the node itself.</summary>
    public const string VisibilityWithPropertyType = "visibility controlled block names a property type: the engine binds the node itself";

    /// <summary>The code of <see cref="VisibilityWithPropertyType" />.</summary>
    public const string VisibilityWithPropertyTypeCode = "visibilityWithPropertyType";

    /// <summary>A material or texture controlled block with no Property Type: nothing names the property to drive.</summary>
    public const string PropertyTypeMissing = "property controlled block without a property type: nothing to bind";

    /// <summary>The code of <see cref="PropertyTypeMissing" />.</summary>
    public const string PropertyTypeMissingCode = "propertyTypeMissing";

    /// <summary>The Property Type names no property of exactly that type in the target's own Properties list (the engine walks no ancestors).</summary>
    public const string PropertyNotOnTarget = "property type names no property of that type in the target's own property list";

    /// <summary>The code of <see cref="PropertyNotOnTarget" />.</summary>
    public const string PropertyNotOnTargetCode = "propertyNotOnTarget";

    /// <summary>A controlled block's stored Controller ref that is not a controller of the Controller Type targeting the bound object.</summary>
    public const string ControllerRefMismatch = "controlled block controller ref is not a controller of the controller type on the bound target";

    /// <summary>The code of <see cref="ControllerRefMismatch" />.</summary>
    public const string ControllerRefMismatchCode = "controllerRefMismatch";

    /// <summary>No controller of the Controller Type and Controller ID sits on the bound object's controller chain.</summary>
    public const string ControllerNotOnChain = "no controller of the controller type and ID on the target's controller chain";

    /// <summary>The code of <see cref="ControllerNotOnChain" />.</summary>
    public const string ControllerNotOnChainCode = "controllerNotOnChain";

    /// <summary>A Controller ID the controller type's engine format does not define (fail closed).</summary>
    public const string ControllerIdUninterpretable = "controller ID not interpretable for the controller type (fail closed)";

    /// <summary>The code of <see cref="ControllerIdUninterpretable" />.</summary>
    public const string ControllerIdUninterpretableCode = "controllerIdUninterpretable";

    /// <summary>A Controller ID that disagrees with the bound controller block's own fields.</summary>
    public const string ControllerIdDisagrees = "controller ID disagrees with the controller block's own fields";

    /// <summary>The code of <see cref="ControllerIdDisagrees" />.</summary>
    public const string ControllerIdDisagreesCode = "controllerIdDisagrees";

    /// <summary>An embedded property controller whose Target is not a property block of the type it drives.</summary>
    public const string ControllerTargetNotProperty = "controller target is not a property block of the type the controller drives";

    /// <summary>The code of <see cref="ControllerTargetNotProperty" />.</summary>
    public const string ControllerTargetNotPropertyCode = "controllerTargetNotProperty";

    /// <summary>An NiMaterialColorController Target Color outside nif.xml's MaterialColor.</summary>
    public const string MaterialColorUndefined = "Target Color outside nif.xml MaterialColor";

    /// <summary>The code of <see cref="MaterialColorUndefined" />.</summary>
    public const string MaterialColorUndefinedCode = "materialColorUndefined";

    /// <summary>An NiTextureTransformController Operation outside nif.xml's TransformMember.</summary>
    public const string OperationUndefined = "Operation outside nif.xml TransformMember";

    /// <summary>The code of <see cref="OperationUndefined" />.</summary>
    public const string OperationUndefinedCode = "operationUndefined";

    /// <summary>An NiTextureTransformController Texture Slot outside nif.xml's TexType.</summary>
    public const string TextureSlotUndefined = "Texture Slot outside nif.xml TexType";

    /// <summary>The code of <see cref="TextureSlotUndefined" />.</summary>
    public const string TextureSlotUndefinedCode = "textureSlotUndefined";

    /// <summary>An NiTextureTransformController on a Shader Textures slot, which the cut-1a material does not type as a layer.</summary>
    public const string ShaderMapSlot = "shader map slot: the shader textures are not document layers";

    /// <summary>The code of <see cref="ShaderMapSlot" />.</summary>
    public const string ShaderMapSlotCode = "shaderMapSlot";

    /// <summary>A texture slot no fed material carries as a layer (the bump map, an absent map, or one the cut-1a reader omitted).</summary>
    public const string TextureSlotNotLayer = "texture slot not a document layer";

    /// <summary>The code of <see cref="TextureSlotNotLayer" />.</summary>
    public const string TextureSlotNotLayerCode = "textureSlotNotLayer";

    /// <summary>The code of a texture transform member the member gate refuses (<see cref="NifModelTextureTransformMember" /> gives the reason).</summary>
    public const string TextureTransformMemberCode = "textureTransformMember";

    /// <summary>A property block no document material was built from (nothing placed draws with it).</summary>
    public const string PropertyNotFed = "property block feeds no document material";

    /// <summary>The code of <see cref="PropertyNotFed" />.</summary>
    public const string PropertyNotFedCode = "propertyNotFed";

    /// <summary>An NiTexturingProperty that did not decode completely, so its map transforms are not readable.</summary>
    public const string TexturingPropertyIncomplete = "texturing property did not decode completely: its map transforms are not readable";

    /// <summary>The code of <see cref="TexturingPropertyIncomplete" />.</summary>
    public const string TexturingPropertyIncompleteCode = "texturingPropertyIncomplete";

    /// <summary>RE-21 step 7: two controlled blocks of one sequence drive one exact property target; the engine blends them, so both stay native.</summary>
    public const string RepeatedPropertyTarget = "repeated property target in sequence: the engine blends them, kept native (RE-21 step 7)";

    /// <summary>The code of <see cref="RepeatedPropertyTarget" />.</summary>
    public const string RepeatedPropertyTargetCode = "repeatedPropertyTarget";

    /// <summary>Two free-running controllers would drive one exact property target in the <c>(controllers)</c> clip.</summary>
    public const string RepeatedEmbeddedPropertyTarget = "repeated property target among free-running controllers";

    /// <summary>The code of <see cref="RepeatedEmbeddedPropertyTarget" />.</summary>
    public const string RepeatedEmbeddedPropertyTargetCode = "repeatedEmbeddedPropertyTarget";

    /// <summary>An NiUVController whose Texture Set is not 0: which layer another set names is not established.</summary>
    public const string UvControllerTextureSet = "NiUVController texture set other than 0: not established";

    /// <summary>The code of <see cref="UvControllerTextureSet" />.</summary>
    public const string UvControllerTextureSetCode = "uvControllerTextureSet";

    /// <summary>An NiUVController whose Target is not a placed geometry drawing with a document material.</summary>
    public const string UvControllerTargetNotGeometry = "NiUVController target is not a placed geometry with a document material";

    /// <summary>The code of <see cref="UvControllerTargetNotGeometry" />.</summary>
    public const string UvControllerTargetNotGeometryCode = "uvControllerTargetNotGeometry";

    /// <summary>An NiUVController whose target's material carries no Base map layer from an NiTexturingProperty.</summary>
    public const string UvControllerNoBaseLayer = "NiUVController target material carries no NiTexturingProperty Base map layer";

    /// <summary>The code of <see cref="UvControllerNoBaseLayer" />.</summary>
    public const string UvControllerNoBaseLayerCode = "uvControllerNoBaseLayer";

    /// <summary>The NativeOnly reason for a slice-2 curve, state or clock refusal.</summary>
    /// <param name="block">The refusal; not <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The reason text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is None or undefined.</exception>
    public static string Reason(NifModelCurveBlock block)
    {
        return block switch
        {
            NifModelCurveBlock.UnknownKeyType => "unknown key type: no Shared form",
            NifModelCurveBlock.EulerRecordCount =>
                "Euler rotation with a record count other than 1: the engine evaluates record 0 only, fail closed " +
                "(RE-20 rule 2)",
            NifModelCurveBlock.EulerAxisKeyType =>
                "Euler axis key type outside LINEAR, QUADRATIC, TBC and CONST (RE-20 rule 2)",
            NifModelCurveBlock.EulerSampledBeforeFirstKey =>
                "Euler axis sampled before its first key (RE-20 rule 7): no Shared extrapolation",
            NifModelCurveBlock.SquadZeroLengthSpan => "zero-length span: the engine's inner point is NaN (RE-24)",
            NifModelCurveBlock.SquadPolicyPs3 => "no measured normalization policy for PS3 (RE-24 covers GECK and X360)",
            NifModelCurveBlock.ZeroQuaternion => "zero quaternion key or Squad inner point: Shared refuses it",
            NifModelCurveBlock.InvalidKeyTimes => "key times not finite or not strictly increasing",
            NifModelCurveBlock.NonFiniteValue => "non-finite key value, tangent, parameter or control point",
            NifModelCurveBlock.NonUnitRotation => "rotation keys outside Shared unit tolerance (SA2)",
            NifModelCurveBlock.BsplineMissingData => "B-spline data or basis did not resolve",
            NifModelCurveBlock.BsplineControlPointCount => "B-spline control-point count outside the degree-3 range",
            NifModelCurveBlock.BsplineInvalidInterval => "B-spline interval not finite or not increasing",
            NifModelCurveBlock.BsplineControlsOutOfRange => "B-spline controls run past the data",
            NifModelCurveBlock.BsplineNegativeHalfRange => "negative B-spline half range",
            NifModelCurveBlock.InactiveController => "inactive controller",
            NifModelCurveBlock.InactiveManager =>
                "inactive manager or multi-target controller: its sequences do not play (RE-22)",
            NifModelCurveBlock.SentinelClockWithCurve => "sentinel clock with curve",
            NifModelCurveBlock.DegenerateClock => "degenerate clock (RE-22)",
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No reason for this outcome.")
        };
    }

    /// <summary>The NativeOnly reason for a slice-5 text-key refusal (the whole NiTextKeyExtraData stays native).</summary>
    /// <param name="block">The refusal; not <see cref="NifModelTextKeyBlock.None" />.</param>
    /// <returns>The reason text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is None or undefined.</exception>
    public static string Reason(NifModelTextKeyBlock block)
    {
        return block switch
        {
            NifModelTextKeyBlock.NotConsumedExactly => "text keys not consumed exactly",
            NifModelTextKeyBlock.NonFiniteTime => "text key time not finite",
            NifModelTextKeyBlock.LabelOutOfRange => "text key label index outside the string table",
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No reason for this outcome.")
        };
    }

    /// <summary>A stable machine code for a slice-5 text-key refusal.</summary>
    /// <param name="block">The refusal; not <see cref="NifModelTextKeyBlock.None" />.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is None or undefined.</exception>
    public static string Code(NifModelTextKeyBlock block)
    {
        return block switch
        {
            NifModelTextKeyBlock.NotConsumedExactly => "textKeysNotExact",
            NifModelTextKeyBlock.NonFiniteTime => "textKeyNonFiniteTime",
            NifModelTextKeyBlock.LabelOutOfRange => "textKeyLabelOutOfRange",
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No code for this outcome.")
        };
    }

    /// <summary>A stable machine code for a slice-2 curve, state or clock refusal.</summary>
    /// <param name="block">The refusal; not <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is None or undefined.</exception>
    public static string Code(NifModelCurveBlock block)
    {
        return block switch
        {
            NifModelCurveBlock.UnknownKeyType => "unknownKeyType",
            NifModelCurveBlock.EulerRecordCount => "eulerRecordCount",
            NifModelCurveBlock.EulerAxisKeyType => "eulerAxisKeyType",
            NifModelCurveBlock.EulerSampledBeforeFirstKey => "eulerSampledBeforeFirstKey",
            NifModelCurveBlock.SquadZeroLengthSpan => "squadZeroLengthSpan",
            NifModelCurveBlock.SquadPolicyPs3 => "squadPolicyPs3",
            NifModelCurveBlock.ZeroQuaternion => "zeroQuaternion",
            NifModelCurveBlock.InvalidKeyTimes => "invalidKeyTimes",
            NifModelCurveBlock.NonFiniteValue => "nonFiniteValue",
            NifModelCurveBlock.NonUnitRotation => "nonUnitRotation",
            NifModelCurveBlock.BsplineMissingData => "bsplineMissingData",
            NifModelCurveBlock.BsplineControlPointCount => "bsplineControlPointCount",
            NifModelCurveBlock.BsplineInvalidInterval => "bsplineInvalidInterval",
            NifModelCurveBlock.BsplineControlsOutOfRange => "bsplineControlsOutOfRange",
            NifModelCurveBlock.BsplineNegativeHalfRange => "bsplineNegativeHalfRange",
            NifModelCurveBlock.InactiveController => "inactiveController",
            NifModelCurveBlock.InactiveManager => "inactiveManager",
            NifModelCurveBlock.SentinelClockWithCurve => "sentinelClockWithCurve",
            NifModelCurveBlock.DegenerateClock => "degenerateClock",
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No code for this outcome.")
        };
    }
}
