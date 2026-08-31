using System.IO;

namespace SupportCaseManager.KlocworkProbe
{
    /// <summary>
    /// Static-analysis probe code. This project is intentionally not referenced by
    /// any production or test project, and none of these methods are executed.
    /// </summary>
    public class KlocworkProbe
    {
        private int count;

        /// <summary>Expected checker: CS.CTOR.VIRTUAL.</summary>
        public KlocworkProbe()
        {
            count = 0;
            Initialize();
        }

        public virtual void Initialize()
        {
        }

        /// <summary>Expected checker: CS.NRE.GEN.MUST.</summary>
        public void TriggerNullDereference()
        {
            KlocworkProbe value = null;
            value.TriggerNullDereference();
        }

        /// <summary>Expected checker: CS.ABV.EXCEPT.</summary>
        public void TriggerArrayBoundsViolation()
        {
            int[] values = new int[5];
            int index = 4;
            ++index;
            values[index] = 10;
        }

        /// <summary>Expected checker: CS.EMPTY.CATCH.</summary>
        public void TriggerEmptyCatch(string path)
        {
            try
            {
                File.OpenRead(path);
            }
            catch (FileNotFoundException)
            {
            }
        }

        /// <summary>Expected checker: CS.LOOP.STR.CONCAT.</summary>
        public string TriggerLoopStringConcat(int repeatCount, string text)
        {
            string result = "";
            for (int index = 0; index < repeatCount; index++)
            {
                result += text;
            }

            return result;
        }

        /// <summary>Expected checker: CS.FLOAT.EQCHECK.</summary>
        public bool TriggerFloatEquality(double left, double right)
        {
            return left == right;
        }

        /// <summary>Expected checker: CS.HIDDEN.MEMBER.PARAM.CLASS.</summary>
        public void TriggerHiddenMember(int count)
        {
            count++;
        }

        public int ReadCount()
        {
            return count;
        }
    }

    /// <summary>Expected checker: CS.IFACE.EMPTY.</summary>
    public interface IKlocworkProbeMarker
    {
    }
}
