# Klocwork C# probe

This project contains intentionally defective C# code for verifying Klocwork
integration-build analysis. It is included in `SupportCaseManager.slnx` so that
`kwinject` captures it, but no production or test project references it.

The methods must not be called or copied into application code. Expected
default-enabled checkers are:

- `CS.NRE.GEN.MUST`
- `CS.ABV.EXCEPT`
- `CS.EMPTY.CATCH`
- `CS.CTOR.VIRTUAL`
- `CS.LOOP.STR.CONCAT`
- `CS.FLOAT.EQCHECK`
- `CS.HIDDEN.MEMBER.PARAM.CLASS`
- `CS.IFACE.EMPTY`
