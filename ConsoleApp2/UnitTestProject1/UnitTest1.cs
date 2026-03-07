using ConsoleApp2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace UnitTestProject1
{
    [TestClass]
    public class DateCalculatorTests
    {
        [TestMethod]
        public void CalculateTwoDates_GivenTwoDates_ReturnsCorrectDifferenceInDays()
        {
            DateCalculator _calculator = new DateCalculator();

            // Arrange
            var date1 = new DateTime(2022, 1, 1);
            var date2 = new DateTime(2022, 1, 31);

            // Act
            var result = _calculator.CalculateTwoDates(date1, date2);

            // Assert
            Assert.AreEqual(30, result);
        }
    }
}
