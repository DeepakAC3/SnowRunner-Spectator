param([string]$HelperPath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom($HelperPath)
$program = $assembly.GetType('SnowFlyer2.Program', $true)
$predicate = $program.GetMethod('IsRetryableMatrixBasisRace', [Reflection.BindingFlags]'NonPublic,Static')
function MatrixBytes([double]$angle, [double]$shift = 0) {
    $c = [single][Math]::Cos($angle)
    $s = [single][Math]::Sin($angle)
    [single[]]$m = @($c,0,$s,0, 0,1,0,0, -$s,0,$c,0, 0,0,0,1)
    $x = 571.8 + $shift
    $y = 122.16
    $z = 800.42
    $m[12] = -($x*$m[0] + $y*$m[4] + $z*$m[8])
    $m[13] = -($x*$m[1] + $y*$m[5] + $z*$m[9])
    $m[14] = -($x*$m[2] + $y*$m[6] + $z*$m[10])
    [byte[]]$bytes = New-Object byte[] 64
    [Buffer]::BlockCopy($m,0,$bytes,0,64)
    return ,$bytes
}
$expected = MatrixBytes 0
$cases = @(
    @{Name='captured-scale coherent rotation'; Actual=(MatrixBytes 0.0021316); Want=$true},
    @{Name='exact matrix'; Actual=(MatrixBytes 0); Want=$false},
    @{Name='strict-success tiny rotation'; Actual=(MatrixBytes 0.00001); Want=$false},
    @{Name='large rotation'; Actual=(MatrixBytes 0.02); Want=$false},
    @{Name='position shifted 1 metre'; Actual=(MatrixBytes 0.0021316 1); Want=$false},
    @{Name='truncated matrix'; Actual=([byte[]]@(0)); Want=$false}
)
[byte[]]$bad = MatrixBytes 0.0021316
[Buffer]::BlockCopy([BitConverter]::GetBytes([single]::NaN),0,$bad,0,4)
$cases += @{Name='nonfinite matrix'; Actual=$bad; Want=$false}
foreach ($case in $cases) {
    $actual = $predicate.Invoke($null, [object[]]@($expected,$case.Actual))
    if ($actual -ne $case.Want) {throw "$($case.Name): expected $($case.Want), got $actual"}
    "PASS: $($case.Name)"
}
