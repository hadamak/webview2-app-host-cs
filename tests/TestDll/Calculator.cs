using System;

namespace TestLib
{
    /// <summary>
    /// テスト用の計算クラス。
    /// リフレクション呼び出しのテストに使用する。
    /// </summary>
    public class Calculator
    {
        /// <summary>
        /// 2つの数値を足し算する。
        /// </summary>
        public static int Add(int a, int b)
        {
            return a + b;
        }
        
        /// <summary>
        /// 2つの数値を引き算する。
        /// </summary>
        public static int Subtract(int a, int b)
        {
            return a - b;
        }
        
        /// <summary>
        /// 結果を通知するイベント。(Action<T> パターンのテスト用)
        /// </summary>
        public static event Action<int> OnResult;
        
        /// <summary>
        /// 引数なしの Action パターンのテスト用
        /// </summary>
        public static event Action OnPing;

        /// <summary>
        /// EventHandler<T> パターンのテスト用
        /// </summary>
        public static event EventHandler<CalculationEventArgs> OnCalculationFinished;
        
        /// <summary>
        /// イベントを発火する。(Action<T>)
        /// </summary>
        public static void TriggerResult(int value)
        {
            OnResult?.Invoke(value);
        }

        /// <summary>
        /// イベントを発火する。(Action)
        /// </summary>
        public static void TriggerPing()
        {
            OnPing?.Invoke();
        }

        /// <summary>
        /// イベントを発火する。(EventHandler<T>)
        /// </summary>
        public static void TriggerCalculationFinished(string operation, int result)
        {
            OnCalculationFinished?.Invoke(null, new CalculationEventArgs { Operation = operation, Result = result });
        }
    }

    public class CalculationEventArgs : EventArgs
    {
        public string Operation { get; set; }
        public int Result { get; set; }
    }
}